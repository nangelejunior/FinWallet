using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Security;
using Microsoft.Extensions.Logging;
namespace FinWallet.Application.Outbox;
public sealed record ReplayOutboxRequest(Guid RequestId, string Reason);
public sealed record ReplayOutboxResult(Guid MessageId, Guid RequestId);
public sealed class ReplayOutboxHandler(IOutboxRepository repository, ICurrentActor actor, ILogger<ReplayOutboxHandler> logger)
{
    public async Task<ReplayOutboxResult> HandleAsync(Guid messageId, ReplayOutboxRequest request, CancellationToken ct = default)
    {
        try
        {
            if (!actor.CanAdministerOutbox) throw new OperationForbiddenException();
            if (messageId == Guid.Empty || request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
                throw new ArgumentException("Identificadores e motivo (1–500 caracteres) obrigatórios.");
            await repository.ReplayAsync(messageId, request.RequestId, request.Reason, actor.Subject, actor.CorrelationId, ct);
            logger.LogInformation("OutboxReplay Actor={Actor} Message={Message} Request={Request} Correlation={Correlation} Result=Scheduled", actor.Subject, messageId, request.RequestId, actor.CorrelationId);
            return new(messageId, request.RequestId);
        }
        catch (Exception e)
        {
            logger.LogWarning("OutboxReplay Actor={Actor} Message={Message} Request={Request} Correlation={Correlation} Result={Result}", actor.Subject, messageId, request.RequestId, actor.CorrelationId, e.GetType().Name);
            throw;
        }
    }
}
