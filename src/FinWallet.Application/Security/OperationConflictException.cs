namespace FinWallet.Application.Security;
public sealed class OperationConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
