using FinWallet.Application.Abstractions.Randomness;

namespace FinWallet.Infrastructure.Randomness;

public sealed class DefaultRandomProvider : IRandomProvider
{
    public double NextDouble() => Random.Shared.NextDouble();
}