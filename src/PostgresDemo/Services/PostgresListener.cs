using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;

namespace PostgresDemo.Services;

/// <summary>A bounded in-memory ring buffer of recent product-change notifications.</summary>
public sealed class NotificationStore
{
    private readonly ConcurrentQueue<JsonElement> _recent = new();
    private const int MaxKept = 50;

    public void Add(JsonElement payload)
    {
        _recent.Enqueue(payload);
        while (_recent.Count > MaxKept && _recent.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<JsonElement> Recent => _recent.ToArray();
}

/// <summary>
/// Postgres LISTEN/NOTIFY, the database's own pub/sub. A trigger on the
/// Products table (see the initial migration) calls
/// <c>pg_notify('product_events', ...)</c> on every insert/update; this
/// hosted service holds one long-lived connection, LISTENs on that channel,
/// and records each payload in the <see cref="NotificationStore"/>.
///
/// <para>
/// One connection, one channel, no polling. If the database is unreachable
/// at startup the listener logs a warning and stops — the API keeps serving;
/// notifications are a feature, not a dependency.
/// </para>
/// <para>
/// The connection string comes from <see cref="IConfiguration"/>, not from
/// the scoped <see cref="AppDbContext"/>: a hosted service is a singleton
/// and cannot consume scoped services.
/// </para>
/// </summary>
public sealed class PostgresListener(
    IConfiguration configuration,
    NotificationStore store,
    ILogger<PostgresListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("PostgresDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No Postgres connection string — LISTEN/NOTIFY listener disabled.");
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(stoppingToken);
            await using var listen = new NpgsqlCommand("LISTEN product_events;", connection);
            await listen.ExecuteNonQueryAsync(stoppingToken);
            logger.LogInformation("Listening for product_events notifications.");
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not start the product_events listener.");
            return;
        }

        connection.Notification += (_, e) =>
        {
            try
            {
                store.Add(JsonDocument.Parse(e.Payload).RootElement.Clone());
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Ignoring malformed notification payload: {Payload}", e.Payload);
            }
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await connection.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "product_events listener stopped unexpectedly.");
        }
    }
}
