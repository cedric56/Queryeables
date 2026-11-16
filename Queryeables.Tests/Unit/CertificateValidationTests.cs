using Queryeables.Abstraction;
using Queryeables.Remote;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Queryeables.Tests.Unit;

/// <summary>TLS: without a validator the system decides; a validator is always called, on every handler.</summary>
public class CertificateValidationTests
{
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
}
