using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FinWallet.Api.Security;

internal static class DevelopmentAuthentication
{
    public static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment()) return;
        var configuration = builder.Configuration;
        // Uma configuração explícita, mesmo parcial, deve ser validada normalmente.
        if (new[] { "Issuer", "Audience", "PublicKeyPem" }
            .Any(key => configuration[$"Authentication:{key}"] is not null)) return;

        var directory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../artifacts/auth"));
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, "private.pem");
        using var rsa = RSA.Create(3072);
        if (File.Exists(privatePath)) rsa.ImportFromPem(File.ReadAllText(privatePath));
        else WritePrivate(privatePath, rsa.ExportPkcs8PrivateKeyPem());
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(Path.Combine(directory, "public.pem"), publicKey);

        const string issuer = "finwallet-local";
        const string audience = "finwallet";
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Issuer"] = issuer,
            ["Authentication:Audience"] = audience,
            ["Authentication:PublicKeyPem"] = publicKey
        });

        string Token(string subject, string customer, string? permission = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object>
            {
                ["iss"] = issuer, ["aud"] = audience, ["sub"] = subject,
                ["customer_id"] = customer, ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600
            };
            if (permission is not null) claims["permission"] = permission;
            var content = $"{Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }))}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}";
            return $"{content}.{Encode(rsa.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }

        var values = new Dictionary<string, string>
        {
            ["Authentication__Issuer"] = issuer,
            ["Authentication__Audience"] = audience,
            ["Authentication__PublicKeyPem"] = publicKey,
            ["TOKEN_A"] = Token("cliente-a", "11111111-1111-1111-1111-111111111111"),
            ["TOKEN_B"] = Token("cliente-b", "22222222-2222-2222-2222-222222222222"),
            ["TOKEN_TECH"] = Token("operador-demo", "33333333-3333-3333-3333-333333333333", "deposit"),
            ["SUMMARY_ADMIN_TOKEN"] = Token("admin-consolidado-demo", "44444444-4444-4444-4444-444444444444", "summary.admin"),
            ["OUTBOX_ADMIN_TOKEN"] = Token("admin-outbox-demo", "55555555-5555-5555-5555-555555555555", "outbox.admin")
        };
        WritePrivate(Path.Combine(directory, "demo.env"), string.Concat(values.Select(pair =>
            $"export {pair.Key}='{pair.Value.Replace("'", "'\\''")}'\n")));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void WritePrivate(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
