using FinWallet.Infrastructure.Messaging;
using Npgsql;

namespace FinWallet.Validation;

public static class ValidationEnvironment
{
    public static string ConnectionString => Environment.GetEnvironmentVariable("FINWALLET_TEST_CONNECTION")
        ?? "Host=localhost;Port=55433;Database=finwallet_validation;Username=finwallet_validation;Password=validation_only;SSL Mode=Disable;Timeout=5;Command Timeout=15";

    public static void EnsureSafeDatabase(string? connection = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection ?? ConnectionString);
        if (builder.Database != "finwallet_validation" || builder.Username != "finwallet_validation")
            throw new InvalidOperationException("Validação destrutiva permitida somente no banco e usuário finwallet_validation.");
    }

    public static RabbitMqOptions Rabbit(string? queue = null) => new()
    {
        HostName = Environment.GetEnvironmentVariable("FINWALLET_TEST_RABBIT_HOST") ?? "localhost",
        Port = int.Parse(Environment.GetEnvironmentVariable("FINWALLET_TEST_RABBIT_PORT") ?? "55672"),
        UserName = "finwallet_validation",
        Password = "validation_only",
        QueueName = queue ?? $"validation.{Guid.NewGuid():N}"
    };
}
