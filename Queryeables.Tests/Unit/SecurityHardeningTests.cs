using Queryeables.Abstraction;
using Queryeables.Remote;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;

namespace Queryeables.Tests.Unit;

/// <summary>An account as an application exposes it: a secret never sent.</summary>
public class HAccount
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string Secret { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string? Role { get; set; }
}

/// <summary>A folder and its notes: a graph whose children have rules of their own.</summary>
public class HFolder
{
    public long Id { get; set; }
    [Required] public string Name { get; set; } = string.Empty;
    [EntityGraph] public List<HNote> HNotes { get; set; } = [];
}

public class HNote
{
    public long Id { get; set; }
    public long HFolderId { get; set; }
    [Required, StringLength(5)] public string Text { get; set; } = string.Empty;
}

/// <summary>The fixes of the November audit, each against the way it was attacked.</summary>
public class SecurityHardeningTests
{
    // ---------- TLS: no validator, the system validates; a validator is always called ----------

    /// <summary>A TLS server on 127.0.0.1 with a self-signed certificate, answering "ok" over HTTP/1.1.</summary>
    private sealed class SelfSignedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public SelfSignedServer()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public X509Certificate2 Certificate { get; }

        public Uri Url => new($"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(Certificate);
                            var buffer = new byte[4096];
                            _ = await tls.ReadAsync(buffer);
                            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                        }
                        catch (Exception)
                        {
                            // the client refused the certificate
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            Certificate.Dispose();
        }
    }

    private static async Task<string> GetAsync(HttpMessageHandler handler, Uri url)
    {
        using var client = new HttpClient(handler, disposeHandler: true);
        return await client.GetStringAsync(url);
    }

    [Fact]
    public async Task Without_a_validator_an_untrusted_certificate_is_refused()
    {
        await using var server = new SelfSignedServer();
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(new SocketsHttpHandler(), server.Url));
    }

    [Fact]
    public async Task A_validator_is_called_on_a_sockets_handler_and_decides()
    {
        await using var server = new SelfSignedServer();
        var pinned = server.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var calls = 0;

        var accepting = CertificateValidation.Attach(new SocketsHttpHandler(), (certificate, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return certificate?.GetCertHashString(HashAlgorithmName.SHA256) == pinned;
        });
        Assert.Equal("ok", await GetAsync(accepting, server.Url));
        Assert.True(calls > 0);

        var refusing = CertificateValidation.Attach(new SocketsHttpHandler(), (_, _, _) => false);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(refusing, server.Url));
    }

    [Fact]
    public async Task A_validator_is_called_on_an_http_client_handler_and_under_delegating_handlers()
    {
        await using var server = new SelfSignedServer();
        var calls = 0;
        var handler = CertificateValidation.Attach(new PassThrough(new HttpClientHandler()), (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return true;
        });

        Assert.Equal("ok", await GetAsync(handler, server.Url));
        Assert.True(calls > 0);
    }

    [Fact]
    public void A_validator_on_a_handler_that_cannot_use_it_is_an_error_not_ignored()
        => Assert.Throws<NotSupportedException>(() => CertificateValidation.Attach(new UnknownHandler(), (_, _, _) => true));

    private sealed class PassThrough(HttpMessageHandler inner) : DelegatingHandler(inner);

    private sealed class UnknownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    // ---------- Notifications: what clients send is not trusted ----------

    private sealed class Recorder(ChangeHubOptions? options = null, ConnectionOwners? owners = null)
        : ChangeBroadcaster<TestClient>(new ChangeFeed<TestClient>(), options: options, owners: owners)
    {
        public List<(EntityChange<TestClient> Change, IReadOnlyList<string> Excluded)> Sent { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        protected override async Task SendAsync(EntityChange<TestClient> record, IReadOnlyList<string> excludedConnections, CancellationToken ct)
        {
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            lock (Sent)
                Sent.Add((record, excludedConnections));
        }
    }

    [Fact]
    public void A_hub_admits_anonymous_connections_only_when_told_to()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var known = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ana")], "test"));
        using var closed = new Recorder();
        using var open = new Recorder(new ChangeHubOptions { AllowAnonymous = true });

        Assert.False(closed.Admits(null));
        Assert.False(closed.Admits(anonymous));
        Assert.True(closed.Admits(known));
        Assert.True(open.Admits(anonymous));
    }

    [Fact]
    public void Changes_reported_by_clients_are_not_relayed_by_default()
    {
        var broadcaster = new Recorder();
        broadcaster.AddSubscriber("attacker");

        Assert.False(broadcaster.RelayFromClient(new EntityChange<TestClient>("7", ChangeType.Delete), "attacker"));
        broadcaster.Dispose();
        Assert.Empty(broadcaster.Sent);
    }

    [Fact]
    public void When_allowed_a_client_change_is_checked_limited_and_only_its_sender_is_left_out()
    {
        var broadcaster = new Recorder(new ChangeHubOptions { RelayClientChanges = true, MaxClientChangesPerSecond = 3, MaxKeyLength = 10 });
        broadcaster.AddSubscriber("a");
        broadcaster.AddSubscriber("victim");

        // "Reload everything", an empty or huge key, a connection that is not subscribed: refused.
        Assert.False(broadcaster.RelayFromClient(new EntityChange<TestClient>(string.Empty, ChangeType.None), "a"));
        Assert.False(broadcaster.RelayFromClient(new EntityChange<TestClient>(new string('9', 11), ChangeType.Update), "a"));
        Assert.False(broadcaster.RelayFromClient(new EntityChange<TestClient>("1", ChangeType.Update), "stranger"));

        // The connections it names (a victim to keep the change from) are ignored.
        var relayed = Enumerable.Range(0, 10)
            .Count(i => broadcaster.RelayFromClient(new EntityChange<TestClient>(i.ToString(), ChangeType.Update, ["victim"]), "a"));
        broadcaster.Dispose();

        Assert.InRange(relayed, 3, 6);                  // 3 per second (6 if the run crossed a second)
        Assert.All(broadcaster.Sent, s => Assert.Equal(["a"], s.Excluded));
    }

    [Fact]
    public void The_connections_left_out_are_never_sent_to_the_clients()
    {
        var broadcaster = new Recorder();
        broadcaster.Relay(new EntityChange<TestClient>("7", ChangeType.Update, ["author-connection"]));
        broadcaster.Dispose();

        var (change, excluded) = Assert.Single(broadcaster.Sent);
        Assert.Equal(["author-connection"], excluded);
        Assert.True(change.Connections is null || change.Connections.Count == 0);
    }

    [Fact]
    public async Task A_full_queue_turns_into_one_synchronization_instead_of_growing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcaster = new Recorder(new ChangeHubOptions { QueueCapacity = 2 }) { Gate = gate };

        for (var i = 0; i < 50; i++)
            broadcaster.Relay(new EntityChange<TestClient>(i.ToString(), ChangeType.Update));
        await Task.Delay(50);
        gate.SetResult();
        broadcaster.Dispose();

        Assert.True(broadcaster.Sent.Count <= 4, $"{broadcaster.Sent.Count} sent for a queue of 2");
        Assert.Contains(broadcaster.Sent, s => s.Change.ChangeType == ChangeType.None && s.Change.EntityKey.Length == 0);
    }

    private static ClaimsPrincipal User(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    [Fact]
    public void A_write_names_a_connection_only_when_it_belongs_to_the_caller()
    {
        var owners = new ConnectionOwners();
        owners.Register("ana-connection", User("ana"));

        Assert.Equal("ana-connection", owners.Verify("ana-connection", User("ana")));
        Assert.Null(owners.Verify("ana-connection", User("bob")));       // Bob cannot keep Ana's notification from her
        Assert.Null(owners.Verify("unknown", User("ana")));
    }

    [Fact]
    public void An_anonymous_user_owns_no_connection()
    {
        var owners = new ConnectionOwners();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        owners.Register("first-anonymous", anonymous);

        // Another anonymous client has the same (empty) key: it must not keep the change from the first one.
        Assert.Null(owners.Verify("first-anonymous", new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(owners.Verify("first-anonymous", null));

        // A connection registered by a user and then by an anonymous caller is no one's any more.
        owners.Register("shared", User("ana"));
        owners.Register("shared", anonymous);
        Assert.Null(owners.Verify("shared", User("ana")));
    }

    [Fact]
    public void An_authenticated_user_without_identity_cannot_be_told_apart()
    {
        Assert.Null(UserKey.Of(new ClaimsPrincipal(new ClaimsIdentity([], "test"))));
        Assert.Equal(string.Empty, UserKey.Of(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.NotEqual(UserKey.Of(User("ana")), UserKey.Of(User("bob")));
    }

    // ---------- Server ----------

    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private static readonly EntityRequestContext Anyone = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static int Depth(Expression expression) => expression switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } binary => 1 + Math.Max(Depth(binary.Left), Depth(binary.Right)),
        _ => 1,
    };

    [Fact]
    public void The_children_of_a_graph_are_validated_with_the_parent()
    {
        var folder = new HFolder { Name = "ok", HNotes = [new HNote { Text = "fine" }, new HNote { Text = "far too long" }, new HNote()] };

        var errors = EntityValidator.ValidateGraph(folder);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.StartsWith("HNotes[", e.ErrorMessage));
        Assert.Contains(errors, e => e.ErrorMessage!.StartsWith("HNotes[1]:", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.ErrorMessage!.StartsWith("HNotes[2]:", StringComparison.Ordinal));
    }

    // ---------- Cache: a row tracked during a load is not lost ----------

    [Fact]
    public async Task A_row_tracked_while_the_cache_reads_its_table_is_kept()
    {
        var rows = new List<TestClient> { new() { Id = 1, Name = "Ana" } };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = false;
        using var cache = new EntityCache<TestClient, long>(() =>
        {
            if (blocking)
            {
                entered.Set();
                release.Wait();                 // the table is read as it was before the write below
            }
            return new InMemoryEntityStore(rows);
        }, c => c.Id);
        await cache.LoadAsync();

        blocking = true;
        var load = Task.Run(() => cache.LoadAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cache.Track(new TestClient { Id = 2, Name = "Bob" });      // written meanwhile, by this client
        release.Set();
        await load;

        Assert.Equal("Bob", cache.Find(2)?.Name);
    }

}

/// <summary>The SQL log: at Debug, and without the values of the parameters unless asked for.</summary>
public class SqlLogTests
{
    private sealed class Capture : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<Capture> RunAsync(Queryeables.Dommel.DbFactoryOptions options)
    {
        var logger = new Capture();
        var factory = new Queryeables.Dommel.DbFactory<Microsoft.Data.Sqlite.SqliteConnection>("Data Source=:memory:", options, logger);
        await using var connection = factory.CreateConnection()!;
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @Email";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Email";
        parameter.Value = "ana@example.org";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync();
        return logger;
    }

    [Fact]
    public async Task The_values_stay_out_of_the_log_by_default()
    {
        var (level, text) = Assert.Single((await RunAsync(new() { LogCommands = true })).Lines);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, level);
        Assert.Contains("@Email", text);
        Assert.DoesNotContain("ana@example.org", text);
    }

    [Fact]
    public async Task The_values_are_logged_only_when_asked_for()
    {
        var (_, text) = Assert.Single((await RunAsync(new() { LogCommands = true, LogParameterValues = true })).Lines);
        Assert.Contains("'ana@example.org'", text);
    }
}
