using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FinWallet.Infrastructure.Persistence.DailySummary;

public sealed class DailySummaryDbContextFactory : IDesignTimeDbContextFactory<DailySummaryDbContext>
{
    public DailySummaryDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var index = Array.IndexOf(args, "--environment");
        if (index >= 0 && index + 1 < args.Length) environment = args[index + 1];
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FinWallet.slnx"))) root = root.Parent;
        var directory = root is null ? Directory.GetCurrentDirectory()
            : Path.Combine(root.FullName, "src", "FinWallet.Api");
        var configuration = new ConfigurationBuilder().SetBasePath(directory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables().Build();
        var connection = configuration.GetConnectionString("DailySummary")
            ?? throw new InvalidOperationException("ConnectionStrings:DailySummary obrigatório");
        return new(new DbContextOptionsBuilder<DailySummaryDbContext>().UseNpgsql(connection).Options);
    }
}
