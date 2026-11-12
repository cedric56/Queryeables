using Queryeables.Abstraction;
using Queryeables.Remote;
using Queryeables.Remote.Offline;
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

/// <summary>An account as an application exposes it: a secret never sent, a note not to search on, a role the server owns.</summary>
public class HAccount
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string Secret { get; set; } = string.Empty;
    [NotQueryable] public string Note { get; set; } = string.Empty;
    [ServerOwned] public string? Role { get; set; }
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

    // ---------- Server: errors, batches, hidden columns, server-owned values, filters, pages ----------

    private readonly MemoryDatabase _database = new();
    private readonly JsonTestSerializer _serializer = new();

    private EntityRequestDispatcher Dispatcher(IEntityStoreFactory? stores = null, bool detailed = false, Action<Exception, string>? log = null)
    {
        stores ??= new EntityStoreFactory(() => new MemoryStore(_database));
        var registry = new EntityHandlerRegistry();
        registry.Register(new EntityHandler<HAccount>(stores, EntityAccessPolicies.AllowAll));
        registry.Register(new EntityHandler<TestClient>(stores, EntityAccessPolicies.AllowAll));
        return new EntityRequestDispatcher(registry, _serializer, stores) { DetailedErrors = detailed, UnexpectedError = log, MaxBatchSize = 3 };
    }

    private static readonly EntityRequestContext Anyone = new(new ClaimsPrincipal(new ClaimsIdentity()));

    [Fact]
    public async Task An_unexpected_error_reaches_the_client_as_a_reference_and_the_log_with_its_message()
    {
        var failing = new EntityStoreFactory(() => throw new InvalidOperationException("relation \"secret_users\" does not exist"));
        (Exception Error, string Reference)? logged = null;
        var dispatcher = Dispatcher(failing, log: (e, r) => logged = (e, r));

        var result = await dispatcher.InsertAsync(EntityName.Of<HAccount>(), _serializer.Serialize(new HAccount { Name = "x" }), Anyone);

        Assert.Equal(EntityErrorCode.Unknown, result.ErrorCode);
        Assert.DoesNotContain("secret_users", result.Exception);
        Assert.NotNull(logged);
        Assert.Contains(logged!.Value.Reference, result.Exception);
        Assert.Contains("secret_users", logged.Value.Error.Message);

        var detailed = await Dispatcher(failing, detailed: true)
            .InsertAsync(EntityName.Of<HAccount>(), _serializer.Serialize(new HAccount { Name = "x" }), Anyone);
        Assert.Contains("secret_users", detailed.Exception);
    }

    [Fact]
    public async Task A_batch_larger_than_allowed_is_refused_before_any_transaction()
    {
        var operations = Enumerable.Range(0, 4)
            .Select(i => new BatchOperation(EntityName.Of<TestClient>(), BatchOperationKind.Insert, _serializer.Serialize(new TestClient { Name = $"n{i}" }), null))
            .ToArray();

        var result = await Dispatcher().ExecuteBatchAsync(operations, Anyone);

        Assert.Equal(EntityErrorCode.InvalidRequest, result.ErrorCode);
        Assert.Empty(_database.All<TestClient>());
    }

    [Fact]
    public async Task A_column_the_client_cannot_read_cannot_be_searched_or_sorted_either()
    {
        await new MemoryStore(_database).InsertAsync(new HAccount { Name = "ana", Secret = "a1b2", Note = "vip" });
        var dispatcher = Dispatcher();
        var type = EntityName.Of<HAccount>();

        foreach (var hidden in new[] { nameof(HAccount.Secret), nameof(HAccount.Note) })
        {
            var search = await dispatcher.SelectAsync(type, [new FilterCondition(hidden, FilterOperator.StartsWith, "a")], any: false, Anyone);
            Assert.Equal(EntityErrorCode.InvalidRequest, search.ErrorCode);
            Assert.Contains("Unknown property", search.Exception);      // as if it did not exist

            var sort = await dispatcher.SelectPagedAsync(type, [], false, 1, 10, [new SortKey(hidden)], Anyone);
            Assert.Equal(EntityErrorCode.InvalidRequest, sort.ErrorCode);
        }

        var allowed = await dispatcher.SelectAsync(type, [new FilterCondition(nameof(HAccount.Name), FilterOperator.Equal, "ana")], false, Anyone);
        Assert.True(allowed.Successful, allowed.Exception);
    }

    [Fact]
    public async Task A_server_owned_value_never_comes_from_the_client()
    {
        var dispatcher = Dispatcher();
        var type = EntityName.Of<HAccount>();

        var inserted = await dispatcher.InsertAsync(type, _serializer.Serialize(new HAccount { Name = "mallory", Role = "admin" }), Anyone);
        Assert.True(inserted.Successful, inserted.Exception);
        var stored = Assert.Single(_database.All<HAccount>());
        Assert.Null(stored.Role);

        stored.Role = "reader";                                  // set by the server's own code
        await new MemoryStore(_database).UpdateAsync(stored);
        var update = new HAccount { Id = stored.Id, Name = "mallory2", Role = "admin" };
        var updated = await dispatcher.UpdateAsync(type, _serializer.Serialize(update), Anyone);
        Assert.True(updated.Successful, updated.Exception);
        var after = Assert.Single(_database.All<HAccount>());
        Assert.Equal("mallory2", after.Name);
        Assert.Equal("reader", after.Role);
    }

    [Fact]
    public async Task A_filter_is_limited_in_values_in_all_and_stays_shallow()
    {
        var dispatcher = Dispatcher();
        var many = Enumerable.Range(0, 1000).Select(i => (object)(long)i).ToList();
        var conditions = Enumerable.Range(0, 3).Select(_ => new FilterCondition(nameof(TestClient.Id), FilterOperator.In, many)).ToArray();

        var refused = await dispatcher.SelectAsync(EntityName.Of<TestClient>(), conditions, any: true, Anyone);
        Assert.Equal(EntityErrorCode.InvalidRequest, refused.ErrorCode);

        // 1000 conditions: a balanced tree, about 10 levels deep, not 1000.
        var wide = Enumerable.Range(0, 1000).Select(i => new FilterCondition(nameof(TestClient.Id), FilterOperator.NotEqual, (long)i)).ToList();
        var predicate = Filters.ToPredicate<TestClient>(wide, any: false);
        Assert.InRange(Depth(predicate.Body), 1, 16);
    }

    private static int Depth(Expression expression) => expression switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } binary => 1 + Math.Max(Depth(binary.Left), Depth(binary.Right)),
        _ => 1,
    };

    [Fact]
    public async Task A_page_beyond_any_table_is_refused_instead_of_overflowing()
    {
        var result = await Dispatcher().SelectPagedAsync(EntityName.Of<TestClient>(), [], false, int.MaxValue, 100, Anyone);
        Assert.Equal(EntityErrorCode.InvalidRequest, result.ErrorCode);
    }

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

    [Fact]
    public async Task An_operation_id_sent_again_for_another_kind_of_write_is_not_answered_with_the_first_result()
    {
        var dispatcher = Dispatcher();
        var id = Guid.NewGuid();
        var type = EntityName.Of<TestClient>();

        var inserted = await dispatcher.WriteOnceAsync(id, type, BatchOperationKind.Insert, _serializer.Serialize(new TestClient { Name = "x" }), Anyone);
        var row = _serializer.Deserialize<TestClient>(inserted.Data!)!;
        var deleted = await dispatcher.WriteOnceAsync(id, type, BatchOperationKind.Delete, _serializer.Serialize(row), Anyone);

        Assert.True(deleted.Successful, deleted.Exception);
        Assert.Empty(_database.All<TestClient>());
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

    // ---------- Offline queue: a write maybe applied is never changed afterwards ----------

    [Fact]
    public void A_write_already_sent_is_not_merged_with_a_later_one_on_its_row()
    {
        var outbox = new Outbox();
        outbox.Add(BatchOperationKind.Insert, "T", "-1", [1]);
        var insert = Assert.Single(outbox.Snapshot());
        Assert.True(outbox.MarkSent(insert.Id));

        outbox.Add(BatchOperationKind.Update, "T", "-1", [2]);       // made while the insert's answer is unknown

        Assert.Equal(2, outbox.Snapshot().Count);
        Assert.Equal([1], outbox.Find(insert.Id)!.Payload);

        outbox.Rekey("T", "-1", "42", payload => [.. payload, 42]);
        Assert.All(outbox.Snapshot(), e => Assert.Equal("42", e.Key));
    }

    [Fact]
    public void A_queue_saved_by_the_previous_version_is_still_read()
    {
        // Version 1: no "sent" flag after each entry.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(2);
            foreach (var key in new[] { "1", "2" })
            {
                writer.Write(Guid.NewGuid().ToByteArray());
                writer.Write("T");
                writer.Write((byte)BatchOperationKind.Update);
                writer.Write(key);
                writer.Write(3);
                writer.Write(new byte[] { 7, 8, 9 });
                writer.Write(DateTime.UtcNow.Ticks);
            }
        }

        var entries = OfflineCodec.ReadEntries(stream.ToArray());

        Assert.Equal(["1", "2"], entries.Select(e => e.Key));
        Assert.All(entries, e => Assert.False(e.Sent));
        Assert.All(entries, e => Assert.Equal([7, 8, 9], e.Payload));

        var again = OfflineCodec.ReadEntries(OfflineCodec.Write([entries[0] with { Sent = true }, entries[1]]));
        Assert.Equal([true, false], again.Select(e => e.Sent));
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
