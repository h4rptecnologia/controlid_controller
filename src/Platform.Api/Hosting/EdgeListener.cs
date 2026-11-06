using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Platform.Api.Hosting;

internal static class EdgeListener
{
    /// <summary>
    /// Porta propria para os gateways, com mTLS obrigatorio. Sem Edge:Port, os gateways nao tem como se conectar.
    /// O proxy do frontend nao deve encaminhar /edge: o TLS precisa terminar aqui para o certificado ser visto.
    /// </summary>
    public static void ConfigureEdgeListener(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<int?>("Edge:Port") is not { } edgePort)
        {
            return;
        }

        var path = builder.Configuration["Edge:CertificatePath"]
            ?? throw new InvalidOperationException("Edge:CertificatePath nao configurado.");
        var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(path, builder.Configuration["Edge:CertificatePassword"]);
        var publicPort = builder.Configuration.GetValue("Edge:PublicPort", 8080);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(publicPort);
            options.ListenAnyIP(edgePort, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                // Nao e bypass: certificado de gateway e autoassinado, sem cadeia a validar. Aqui so se exige que o
                // cliente prove a posse da chave no handshake; EdgeGatewayAuthenticationHandler compara a chave com a
                // identidade cadastrada em cada requisicao, e chave desconhecida nao acessa nada.
                https.ClientCertificateValidation = (_, _, _) => true;
            }));
        });
    }
}
