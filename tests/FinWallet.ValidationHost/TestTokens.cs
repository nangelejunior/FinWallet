using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace FinWallet.Validation;

public static class TestTokens
{
    private static readonly RSA Key = RSA.Create(2048);
    public static string PublicKey => Key.ExportSubjectPublicKeyInfoPem();
    public static string Issue(Guid customer, bool technical = false, string issuer = "finwallet-validation",
        string audience = "finwallet", bool expired = false, bool includeCustomer = true, bool summaryAdmin = false, bool outboxAdmin = false)
    {
        var claims = new List<Claim> { new("sub", technical ? "technical" : customer.ToString()) };
        if (includeCustomer) claims.Add(new("customer_id", customer.ToString()));
        if (technical) claims.Add(new("permission", "deposit"));
        if (outboxAdmin) claims.Add(new("permission", "outbox.admin"));
        if (summaryAdmin) claims.Add(new("permission", "summary.admin"));
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            now.AddHours(-1), expired ? now.AddMinutes(-1) : now.AddHours(1),
            new SigningCredentials(new RsaSecurityKey(Key), SecurityAlgorithms.RsaSha256)));
    }
    public static void Authenticate(HttpClient client, Guid customer, bool technical = false) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Issue(customer, technical));

    public static HttpRequestMessage Request(HttpMethod method, string path, Guid customer, object? body = null, bool technical = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", Issue(customer, technical));
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        return request;
    }
}
