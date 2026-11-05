using FinWallet.Application.Security;
namespace FinWallet.Validation;

// Registered only in the validation assemblies. Production has no bypass switch.
public sealed class TestActor : ICurrentActor
{
    public Guid CustomerId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public string Subject => "validation";
    public string CorrelationId => "validation";
    public bool CanDeposit { get; set; } = true;
}
