namespace FinWallet.Application.Security;

public interface ICurrentActor
{
    Guid CustomerId { get; }
    string Subject { get; }
    string CorrelationId { get; }
    bool CanAdministerOutbox => false;
    bool CanDeposit { get; }
}

public sealed class OperationForbiddenException : Exception
{
    public OperationForbiddenException() : base("Operação não permitida.") { }
}
