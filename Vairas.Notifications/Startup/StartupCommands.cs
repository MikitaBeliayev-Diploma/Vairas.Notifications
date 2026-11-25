using Npgsql;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Startup;

/// <summary>Operator commands that run instead of the web host (`--initialize`, `--purge-content`, `--migrate`, `--grant-*`).</summary>
public static class StartupCommands
{
    private const string MigrationConnectionVariable = "VAIRAS_MIGRATION_CONNECTION";

    /// <summary>Runs the requested command and returns true, or returns false when the web host should start.</summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (Has(args, "--initialize")) await InitializeAsync();
        else if (Has(args, "--purge-content")) await PurgeContentAsync(args);
        else if (Has(args, "--migrate")) await DatabaseMigrator.MigrateAsync(MigrationConnection());
        else if (Has(args, "--grant-runtime") || Has(args, "--grant-worker")) await GrantAsync(MigrationConnection(), Has(args, "--grant-worker") ? "worker-grants.sql" : "runtime-grants.sql");
        else return false;
        return true;
    }

    private static bool Has(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

    private static string MigrationConnection() =>
        Environment.GetEnvironmentVariable(MigrationConnectionVariable) ?? throw new InvalidOperationException("Supply VAIRAS_MIGRATION_CONNECTION.");

    private static async Task InitializeAsync()
    {
        var connectionString = await DatabaseProvisioning.ProvisionAsync();
        await DatabaseMigrator.MigrateAsync(connectionString);
        foreach (var script in new[] { "runtime-grants.sql", "worker-grants.sql" })
            await GrantAsync(connectionString, script);
    }

    private static async Task GrantAsync(string connectionString, string script)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "database", script)), connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task PurgeContentAsync(string[] args)
    {
        var connection = MigrationConnection();
        if (!DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("VAIRAS_RETENTION_BEFORE"), out var cutoff))
            throw new InvalidOperationException("Supply VAIRAS_RETENTION_BEFORE.");
        var apply = Has(args, "--apply");
        var count = await ContentRetention.RunAsync(connection, cutoff, Environment.GetEnvironmentVariable("VAIRAS_RETENTION_REASON") ?? "", apply);
        Console.WriteLine($"Terminal content rows: {count}; applied: {apply}.");
    }
}
