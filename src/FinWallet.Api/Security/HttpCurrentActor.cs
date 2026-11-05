using FinWallet.Application.Security;

namespace FinWallet.Api.Security;

public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private System.Security.Claims.ClaimsPrincipal User => accessor.HttpContext?.User
        ?? throw new OperationForbiddenException();
    public Guid CustomerId => Guid.TryParse(User.FindFirst("customer_id")?.Value, out var id) && id != Guid.Empty
        ? id : throw new OperationForbiddenException();
    public string Subject => User.FindFirst("sub")?.Value ?? throw new OperationForbiddenException();
    public string CorrelationId => accessor.HttpContext?.TraceIdentifier ?? "unavailable";
    public bool CanAdministerOutbox => User.HasClaim("permission", "outbox.admin");
    public bool CanDeposit => User.HasClaim("permission", "deposit");
}
