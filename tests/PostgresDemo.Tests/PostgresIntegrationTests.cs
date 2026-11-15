using Microsoft.EntityFrameworkCore;
using Npgsql;
using PostgresDemo.Data;
using PostgresDemo.Services;

namespace PostgresDemo.Tests;

/// <summary>
/// Integration tests against a real Postgres. They run in CI, where the
/// workflow starts a <c>postgres:16</c> service container; locally they need
/// Postgres running (<c>docker compose up -d postgres</c>) and fail with a
/// clear message when it isn't. JSONB, full-text search and advisory locks
/// are Postgres behaviour — faking them in SQLite would test nothing.
/// </summary>
public sealed class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FullTextSearch_FindsProductByDescriptionWords()
    {
        var results = await _fixture.Repository.SearchAsync(
            search: "noise-cancelling wireless", tag: null, specKey: null, specValue: null,
            page: 1, pageSize: 20);

        Assert.Contains(results, p => p.Sku == "HP-300");
    }

    [Fact]
    public async Task FullTextSearch_PrefixMatches_PartialWord()
    {
        var results = await _fixture.Repository.SearchAsync(
            search: "wireles", tag: null, specKey: null, specValue: null,
            page: 1, pageSize: 20);

        Assert.Contains(results, p => p.Sku == "HP-300");
    }

    [Fact]
    public async Task TagFilter_FindsProductsWithTag()
    {
        var results = await _fixture.Repository.SearchAsync(
            search: null, tag: "usb-c", specKey: null, specValue: null,
            page: 1, pageSize: 20);

        Assert.Equal(3, results.Count);
        Assert.All(results, p => Assert.Contains("usb-c", p.Tags));
    }

    [Fact]
    public async Task JsonbSpecFilter_FindsProductBySpecValue()
    {
        var results = await _fixture.Repository.SearchAsync(
            search: null, tag: null, specKey: "color", specValue: "black",
            page: 1, pageSize: 20);

        Assert.Single(results);
        Assert.Equal("HP-300", results[0].Sku);
    }

    [Fact]
    public async Task JsonbSpecValue_ReadsSingleValueWithoutLoadingRow()
    {
        var headphones = await _fixture.Repository.SearchAsync(
            search: "headphones", tag: null, specKey: null, specValue: null,
            page: 1, pageSize: 1);

        var battery = await _fixture.Repository.GetSpecValueAsync(headphones[0].Id, "battery_hours");

        Assert.Equal("40", battery);
    }

    [Fact]
    public async Task IdempotentOrder_SameKeyTwice_ReturnsSameOrderOnce()
    {
        var key = $"test-{Guid.NewGuid():N}";
        var first = await _fixture.Orders.CreateOrderAsync(1, 2, key);
        var second = await _fixture.Orders.CreateOrderAsync(1, 2, key);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Order!.Id, second.Order!.Id);

        var count = await _fixture.CountOrdersAsync(key);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IdempotentOrder_ConcurrentSameKey_CreatesExactlyOneOrder()
    {
        var key = $"test-{Guid.NewGuid():N}";
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _fixture.Orders.CreateOrderAsync(1, 1, key))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Created);
        Assert.Equal(1, await _fixture.CountOrdersAsync(key));
        Assert.All(results, r => Assert.NotNull(r.Order));
        Assert.Single(results.Select(r => r.Order!.Id).Distinct());
    }

    [Fact]
    public async Task CompareEfVsDapper_ReturnsSameRows()
    {
        // "usb" matches three seeded products by name/description
        // (MN-200, DK-500, SP-700) — tags are NOT in the search vector.
        var result = await _fixture.Repository.CompareEfVsDapperAsync("usb");

        Assert.True(result.RowCountsMatch);
        Assert.Equal(3, result.EfCoreRows);
        Assert.Equal(3, result.DapperRows);
    }

    [Fact]
    public async Task CreateProduct_TriggersSearchVector_AndIsSearchableImmediately()
    {
        var product = new PostgresDemo.Models.Product
        {
            Sku = $"TEST-{Guid.NewGuid():N}"[..12],
            Name = "Quantum Flux Capacitor",
            Description = "A one-of-a-kind test gadget for search verification.",
            Price = 1.23m,
            Stock = 1,
            Category = "Test",
            Tags = ["test-gadget"],
            Specs = new(),
        };
        await _fixture.Repository.CreateAsync(product);

        var found = await _fixture.Repository.SearchAsync(
            search: "quantum flux", tag: null, specKey: null, specValue: null,
            page: 1, pageSize: 20);

        Assert.Contains(found, p => p.Id == product.Id);
    }
}

/// <summary>
/// One shared database for the integration tests: created fresh from the
/// migration, seeded, and dropped when the run ends.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DefaultConnection =
        "Host=localhost;Port=5432;Database=postgresdemo_test;Username=postgres;Password=postgres";

    private readonly string _connectionString =
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION_STRING") ?? DefaultConnection;

    // One data source for the fixture: it carries the EnableDynamicJson
    // opt-in and the credentials, and the repository reuses it for Dapper.
    private readonly NpgsqlDataSource _dataSource;

    private AppDbContext? _context;

    public bool Available { get; private set; }

    public PostgresFixture()
    {
        var builder = new NpgsqlDataSourceBuilder(_connectionString);
        builder.EnableDynamicJson();
        _dataSource = builder.Build();
    }

    public ProductRepository Repository => new(CreateContext(), _dataSource);

    public OrderService Orders => new(CreateContext());

    public async Task<int> CountOrdersAsync(string key)
    {
        await using var db = CreateContext();
        return await db.Orders.CountAsync(o => o.IdempotencyKey == key);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_dataSource)
            .Options;
        return new AppDbContext(options);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(_connectionString);
            await probe.OpenAsync();
            await probe.CloseAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Postgres test database is not reachable. Start it with " +
                "'docker compose up -d postgres' (or set POSTGRES_TEST_CONNECTION_STRING). " +
                $"Tried: {_connectionString}", ex);
        }

        _context = CreateContext();
        await _context.Database.EnsureDeletedAsync();
        await _context.Database.MigrateAsync();
        await SeedData.SeedAsync(_context);
        Available = true;
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.Database.EnsureDeletedAsync();
            await _context.DisposeAsync();
        }
    }
}
