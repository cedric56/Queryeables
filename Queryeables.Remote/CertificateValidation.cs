using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Queryeables.Remote;

/// <summary>
/// Plugs a <see cref="CertificateValidator"/> into the handlers the transports use. Without a validator, nothing is
/// changed: the system validates the server certificate (chain, expiry, host name), on every platform.
/// </summary>
/// <remarks>
/// A validator replaces the system's decision, it does not add to it: to pin a certificate and still refuse an invalid
/// one, check <c>errors == SslPolicyErrors.None</c> in it too.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public static class CertificateValidation
{
    /// <summary>
    /// The callback that asks <paramref name="validator"/>. Exposed for the transports and their tests.
    /// </summary>
    public static RemoteCertificateValidationCallback Callback(CertificateValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return (_, certificate, chain, errors) => validator(ToCertificate2(certificate), chain, errors);
    }

    /// <summary>
    /// Makes <paramref name="handler"/> ask <paramref name="validator"/>: a <see cref="SocketsHttpHandler"/> (the default
    /// outside the browser), an <see cref="HttpClientHandler"/>, or one of them under <see cref="DelegatingHandler"/>s.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A handler of another kind: the validator would be silently ignored, and a pinned certificate not checked.
    /// </exception>
    public static HttpMessageHandler Attach(HttpMessageHandler handler, CertificateValidator validator)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(validator);

        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.SslOptions.RemoteCertificateValidationCallback = Callback(validator);
                return handler;
            case HttpClientHandler http:
                http.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => validator(certificate, chain, errors);
                return handler;
            case DelegatingHandler { InnerHandler: { } inner }:
                Attach(inner, validator);
                return handler;
            default:
                throw new NotSupportedException(
                    $"A certificate validator was given, but {handler.GetType().Name} cannot use it: give a SocketsHttpHandler or an HttpClientHandler.");
        }
    }

    /// <summary>Makes a WebSocket ask <paramref name="validator"/> (SignalR's WebSocket transport does not use the HTTP handler).</summary>
    public static void Attach(ClientWebSocketOptions options, CertificateValidator validator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        options.RemoteCertificateValidationCallback = Callback(validator);
    }

    private static X509Certificate2? ToCertificate2(X509Certificate? certificate) => certificate switch
    {
        null => null,
        X509Certificate2 certificate2 => certificate2,
        _ => X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()),
    };
}
