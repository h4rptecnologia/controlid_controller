using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Platform.Shared.Contracts;

namespace Platform.EdgeGateway;

/// <summary>Quais enderecos sao equipamentos deste gateway. Atualizado pela central a cada ciclo.</summary>
public sealed class DeviceDirectory
{
    private volatile Dictionary<IPAddress, Guid> byAddress = [];

    public void Update(IEnumerable<EdgeDevice> devices) =>
        byAddress = devices
            .Where(d => IPAddress.TryParse(d.Host, out _))
            .GroupBy(d => IPAddress.Parse(d.Host))
            .ToDictionary(g => g.Key, g => g.First().DeviceId);

    public Guid? Resolve(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return byAddress.TryGetValue(normalized, out var id) ? id : null;
    }
}

public sealed class MonitorCounters
{
    private readonly ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);

    public void Add(string name) => counts.AddOrUpdate(name, 1, (_, value) => value + 1);

    public long Get(string name) => counts.GetValueOrDefault(name);
}

/// <summary>
/// Recebe as notificacoes do terminal na rede local. Responde sucesso so depois de gravar: sem isso, um evento
/// confirmado poderia se perder. A autenticidade vem do endereco de origem na VLAN, porque o protocolo nao oferece
/// assinatura; forjar IP dentro da VLAN continua possivel (risco residual em docs/entregas/E02.4-monitor.md).
/// </summary>
public static class MonitorListener
{
    // Levam biometria, foto, PIN ou senha. O corpo nem e lido, e o terminal recebe resposta para nao ficar reenviando.
    private static readonly HashSet<string> DiscardWithoutReading = new(StringComparer.Ordinal)
    {
        "template", "user_image", "face_template", "card", "pin", "password", "access_photo",
    };

    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal)
    {
        "dao", "door", "secbox", "catra_event", "operation_mode", "usb_drive", "device_is_alive",
    };

    public static WebApplication Build(int port, string pathPrefix, DeviceDirectory directory, EventSpool spool, MonitorCounters counters, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Listen(IPAddress.Any, port, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        var prefix = "/" + pathPrefix.Trim('/');

        app.Run(async context =>
        {
            var deviceId = directory.Resolve(context.Connection.RemoteIpAddress);
            if (deviceId is not { } device)
            {
                counters.Add("unknown_source");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;
            if (!HttpMethods.IsPost(context.Request.Method) || !path.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var route = path[(prefix.Length + 1)..];
            if (DiscardWithoutReading.Contains(route))
            {
                counters.Add("discarded_sensitive");
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            if (!Accepted.Contains(route))
            {
                counters.Add("unknown_route");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            byte[] body;
            try
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                body = buffer.ToArray();
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                counters.Add("too_large");
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            var events = MonitorNormalizer.Normalize(route, body, device, time.GetUtcNow());
            if (events is null)
            {
                counters.Add("invalid_body");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            foreach (var item in events)
            {
                if (!await spool.TryAppendAsync(item, context.RequestAborted))
                {
                    // Sem espaco nao confirma: o evento continua no terminal e o backfill recupera.
                    counters.Add("spool_full");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
            }

            counters.Add("accepted");
            context.Response.StatusCode = StatusCodes.Status200OK;
        });

        return app;
    }
}
