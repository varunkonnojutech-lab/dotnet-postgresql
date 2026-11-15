using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PostgresDemo.Data;
using PostgresDemo.Models;
using PostgresDemo.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgresDb")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgresDb is not configured.");

// Npgsql 8 made dynamic JSON serialization opt-in: the Dictionary<string,
// JsonElement> Specs column mapped to jsonb needs it. Without this, the
// first insert throws NotSupportedException.
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddHostedService<PostgresListener>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// The API owns its schema: apply migrations on startup (with a short retry
// so `docker compose up` works even if Postgres is still starting), then seed.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var migrated = false;
    for (var attempt = 1; attempt <= 5 && !migrated; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            migrated = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Migration attempt {Attempt}/5 failed — retrying in 2s.", attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    if (migrated)
    {
        await SeedData.SeedAsync(db);
    }
    else
    {
        logger.LogError("Could not apply migrations — the API will start without a database.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new
{
    name = "PostgresDemo",
    description = "PostgreSQL in .NET 8: JSONB, full-text search, arrays, advisory locks, LISTEN/NOTIFY.",
    endpoints = new[]
    {
        "GET /health",
        "GET /products?search=&tag=&specKey=&specValue=",
        "GET /products/{id}",
        "POST /products",
        "GET /products/{id}/specs/{key}",
        "POST /orders (Idempotency-Key header)",
        "GET /orders/{id}",
        "GET /compare?search=",
        "GET /notifications",
    },
    docs = "See README.md and docs/",
}));

app.MapGet("/health", async (AppDbContext db) =>
{
    var ok = await db.Database.CanConnectAsync();
    return ok
        ? Results.Ok(new { status = "healthy", database = "postgres" })
        : Results.Problem("Cannot reach Postgres.", statusCode: 503);
});

var products = app.MapGroup("/products");

products.MapGet("/", async (
    ProductRepository repo,
    string? search, string? tag, string? specKey, string? specValue,
    int page = 1, int pageSize = 20, CancellationToken ct = default) =>
{
    if (page < 1 || pageSize is < 1 or > 100)
    {
        return Results.BadRequest("page must be >= 1 and pageSize between 1 and 100.");
    }

    var results = await repo.SearchAsync(search, tag, specKey, specValue, page, pageSize, ct);
    return Results.Ok(results);
});

products.MapGet("/{id:int}", async (ProductRepository repo, int id, CancellationToken ct) =>
    await repo.GetByIdAsync(id, ct) is { } product
        ? Results.Ok(product)
        : Results.NotFound());

products.MapPost("/", async (ProductRepository repo, CreateProductRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Sku) || string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Sku and Name are required.");
    }

    var product = new Product
    {
        Sku = request.Sku.Trim(),
        Name = request.Name.Trim(),
        Description = request.Description ?? string.Empty,
        Price = request.Price,
        Stock = request.Stock,
        Category = request.Category ?? string.Empty,
        Tags = request.Tags ?? [],
        Specs = request.Specs ?? new(),
    };

    await repo.CreateAsync(product, ct);
    return Results.Created($"/products/{product.Id}", product);
});

products.MapGet("/{id:int}/specs/{key}", async (ProductRepository repo, int id, string key, CancellationToken ct) =>
{
    var value = await repo.GetSpecValueAsync(id, key, ct);
    return value is null ? Results.NotFound() : Results.Ok(new { key, value });
});

app.MapPost("/orders", async (
    OrderService orders, CreateOrderRequest request, HttpContext http, CancellationToken ct) =>
{
    if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues)
        || string.IsNullOrWhiteSpace(keyValues.FirstOrDefault()))
    {
        return Results.BadRequest("Idempotency-Key header is required.");
    }

    if (request.Quantity < 1)
    {
        return Results.BadRequest("Quantity must be at least 1.");
    }

    var result = await orders.CreateOrderAsync(request.ProductId, request.Quantity, keyValues[0]!, ct);
    return result switch
    {
        { NotFound: true } => Results.NotFound($"Product {request.ProductId} does not exist."),
        { Conflict: true } => Results.Conflict("Another request is creating this order — retry with the same key."),
        { Created: true } => Results.Created($"/orders/{result.Order!.Id}", result.Order),
        _ => Results.Ok(result.Order),
    };
});

app.MapGet("/orders/{id:int}", async (OrderService orders, int id, CancellationToken ct) =>
    await orders.GetByIdAsync(id, ct) is { } order
        ? Results.Ok(order)
        : Results.NotFound());

app.MapGet("/compare", async (ProductRepository repo, string search, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(search))
    {
        return Results.BadRequest("search is required, e.g. /compare?search=wireless.");
    }

    var result = await repo.CompareEfVsDapperAsync(search, ct);
    return Results.Ok(result);
});

app.MapGet("/notifications", (NotificationStore store) =>
    Results.Ok(store.Recent));

app.Run();

public sealed record CreateProductRequest(
    string Sku,
    string Name,
    string? Description,
    decimal Price,
    int Stock,
    string? Category,
    string[]? Tags,
    Dictionary<string, JsonElement>? Specs);

public sealed record CreateOrderRequest(int ProductId, int Quantity);
