namespace Queryeables.Tests.Unit;

/// <summary>The SQL log: at Debug, and without the values of the parameters unless asked for.</summary>
public class SqlLogTests
{
    private sealed class Capture : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<Capture> RunAsync(Queryeables.Dommel.DbFactoryOptions options)
    {
        var logger = new Capture();
        var factory = new Queryeables.Dommel.DbFactory<Microsoft.Data.Sqlite.SqliteConnection>("Data Source=:memory:", options, logger);
        await using var connection = factory.CreateConnection()!;
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @Email";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Email";
        parameter.Value = "ana@example.org";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync();
        return logger;
    }

    [Fact]
    public async Task The_values_stay_out_of_the_log_by_default()
    {
        var (level, text) = Assert.Single((await RunAsync(new() { LogCommands = true })).Lines);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, level);
        Assert.Contains("@Email", text);
        Assert.DoesNotContain("ana@example.org", text);
    }

    [Fact]
    public async Task The_values_are_logged_only_when_asked_for()
    {
        var (_, text) = Assert.Single((await RunAsync(new() { LogCommands = true, LogParameterValues = true })).Lines);
        Assert.Contains("'ana@example.org'", text);
    }
}
