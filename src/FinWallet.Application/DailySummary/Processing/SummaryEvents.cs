using System.Text.Json;
using FinWallet.Application.IntegrationEvents;
namespace FinWallet.Application.DailySummary.Processing;

public static class SummaryEvents
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static object Decode(string type, string payload)
    {
        object value;
        try
        {
            value = type switch
            {
                PostedEvent.Type => JsonSerializer.Deserialize<PostedEvent>(payload, Json) ?? throw new InvalidDataException("Evento nulo"),
                WalletRegistered.Type => JsonSerializer.Deserialize<WalletRegistered>(payload, Json) ?? throw new InvalidDataException("Evento nulo"),
                _ => throw new InvalidDataException("Tipo/versão desconhecido")
            };
        }
        catch (JsonException e) { throw new InvalidDataException("JSON inválido", e); }
        if (value is WalletRegistered w)
        {
            ValidatePosition(new(w.WalletId, w.CustomerId, w.Currency, 0, 0));
            if (w.EventId != w.WalletId) throw new InvalidDataException("ID de carteira inconsistente");
        }
        if (value is PostedEvent p)
        {
            if (p.EventId == Guid.Empty || p.EventId != p.TransactionId || p.PostedAt == default ||
                p.PostedAt > DateTimeOffset.UtcNow.AddMinutes(1) || p.Positions is null || p.Positions.Length is < 1 or > 2 ||
                p.Positions.Select(x => x.WalletId).Distinct().Count() != p.Positions.Length)
                throw new InvalidDataException("Identidade/data/partidas inválidas");
            foreach (var position in p.Positions)
            {
                ValidatePosition(position);
                if ((position.Credits == 0) == (position.Debits == 0)) throw new InvalidDataException("Partida inválida");
            }
            if (p.Positions.Select(x => x.Currency).Distinct().Count() != 1 ||
                (p.Positions.Length == 1 && p.Positions[0].Debits != 0) ||
                (p.Positions.Length == 2 && p.Positions.Sum(x => x.Credits) != p.Positions.Sum(x => x.Debits)))
                throw new InvalidDataException("Lançamento desequilibrado");
        }
        return value;
    }
    private static void ValidatePosition(WalletPosition p)
    {
        if (p.WalletId == Guid.Empty || p.CustomerId == Guid.Empty || !FinWallet.Domain.Common.CurrencyCode.IsCanonical(p.Currency) ||
            p.Credits < 0 || p.Debits < 0 || decimal.Round(p.Credits, 4) != p.Credits || decimal.Round(p.Debits, 4) != p.Debits)
            throw new InvalidDataException("Posição inválida");
    }
}
