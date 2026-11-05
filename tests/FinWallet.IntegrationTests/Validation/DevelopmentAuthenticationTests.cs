using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinWallet.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace FinWallet.IntegrationTests.Validation;

public sealed class DevelopmentAuthenticationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"finwallet-auth-{Guid.NewGuid():N}");

    private WebApplicationBuilder Builder(string environment = "Development")
    {
        var content = Path.Combine(_root, "src", "DailySummary");
        Directory.CreateDirectory(content);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment, ContentRootPath = content
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        return builder;
    }

    [Fact]
    public void Development_PreparesCredentialsWithoutScripts_AndReusesKey()
    {
        var first = Builder();
        DevelopmentAuthentication.Configure(first);
        var second = Builder();
        DevelopmentAuthentication.Configure(second);
        Assert.Equal("finwallet-local", second.Configuration["Authentication:Issuer"]);
        Assert.Equal("finwallet", second.Configuration["Authentication:Audience"]);
        Assert.Equal(first.Configuration["Authentication:PublicKeyPem"], second.Configuration["Authentication:PublicKeyPem"]);

        var lines = File.ReadAllLines(Path.Combine(_root, "artifacts", "auth", "demo.env"));
        var token = lines.Single(line => line.StartsWith("export SUMMARY_ADMIN_TOKEN=", StringComparison.Ordinal))
            .Split('=', 2)[1].Trim('\'').Split('.');
        using var rsa = RSA.Create();
        rsa.ImportFromPem(second.Configuration["Authentication:PublicKeyPem"]!);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"), Decode(token[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var payload = JsonDocument.Parse(Decode(token[1]));
        Assert.Equal("summary.admin", payload.RootElement.GetProperty("permission").GetString());
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public void ExplicitConfiguration_AndNonDevelopment_DoNotGenerateCredentials(string environment, bool explicitIssuer)
    {
        var builder = Builder(environment);
        if (explicitIssuer) builder.Configuration["Authentication:Issuer"] = "external";
        DevelopmentAuthentication.Configure(builder);
        Assert.Null(builder.Configuration["Authentication:PublicKeyPem"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "artifacts")));
        if (explicitIssuer) Assert.Equal("external", builder.Configuration["Authentication:Issuer"]);
    }

    private static byte[] Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
