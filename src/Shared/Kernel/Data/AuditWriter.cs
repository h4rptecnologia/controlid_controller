using System.Text.Json;

namespace Platform.Shared.Kernel.Data;

public sealed record AuditEntry(
    Guid? ActorId,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Outcome,
    Guid? CorrelationId,
    string? Reason = null,
    object? Metadata = null);

public static class AuditWriter
{
    public const string Success = "success";
    public const string Denied = "denied";

    /// <summary>Grava na mesma transacao da mudanca. Metadados nao podem conter segredo nem dado biometrico.</summary>
    public static Task WriteAsync(TenantTransaction tx, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(entry);

        return tx.ExecuteAsync(
            """
            INSERT INTO hub.audit_log (tenant_id, actor_id, action, resource_type, resource_id, reason, outcome, correlation_id, metadata)
            VALUES (@TenantId, @ActorId, @Action, @ResourceType, @ResourceId, @Reason, @Outcome, @CorrelationId, @Metadata::jsonb)
            """,
            new
            {
                tx.TenantId,
                entry.ActorId,
                entry.Action,
                entry.ResourceType,
                entry.ResourceId,
                entry.Reason,
                entry.Outcome,
                entry.CorrelationId,
                Metadata = JsonSerializer.Serialize(entry.Metadata ?? new { }),
            });
    }
}

public static class OutboxWriter
{
    /// <summary>Publica na mesma transacao do agregado. Payload apenas com metadados e referencias.</summary>
    public static Task EnqueueAsync(TenantTransaction tx, string topic, Guid aggregateId, Guid? correlationId, object? payload = null)
    {
        ArgumentNullException.ThrowIfNull(tx);

        var envelope = JsonSerializer.Serialize(new
        {
            event_id = Guid.NewGuid(),
            schema_version = 1,
            tenant_id = tx.TenantId,
            aggregate_id = aggregateId,
            correlation_id = correlationId,
            occurred_at = DateTimeOffset.UtcNow,
            type = topic,
            payload = payload ?? new { },
        });

        return tx.ExecuteAsync(
            "INSERT INTO hub.outbox (tenant_id, topic, aggregate_id, payload) VALUES (@TenantId, @topic, @aggregateId, @envelope::jsonb)",
            new { tx.TenantId, topic, aggregateId, envelope });
    }
}
