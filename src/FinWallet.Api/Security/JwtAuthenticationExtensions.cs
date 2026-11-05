using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace FinWallet.Api.Security;

internal static class JwtAuthenticationExtensions
{
    internal static IServiceCollection AddFinancialJwtAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection("Authentication");
        var issuer = auth["Issuer"] ?? throw new InvalidOperationException("Authentication:Issuer obrigatório.");
        var audience = auth["Audience"] ?? throw new InvalidOperationException("Authentication:Audience obrigatório.");
        var publicKey = auth["PublicKeyPem"] ?? throw new InvalidOperationException("Authentication:PublicKeyPem obrigatório.");
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);

        services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new()
            {
                OnTokenValidated = context =>
                {
                    var user = context.Principal!;
                    if (string.IsNullOrWhiteSpace(user.FindFirst("sub")?.Value) ||
                        !Guid.TryParse(user.FindFirst("customer_id")?.Value, out var id) || id == Guid.Empty)
                        context.Fail("Identidade inválida.");
                    return Task.CompletedTask;
                }
            };
        });
        return services;
    }
}
