using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PostgresDemo.Data;
using PostgresDemo.Models;

namespace PostgresDemo.Services;

/// <summary>
/// Product reads. Each method shows the Postgres-native way to ask the
/// question — full-text search, array containment, JSONB operators — and
/// where raw SQL is the honest tool instead of fighting LINQ.
/// </summary>
public sealed class ProductRepository(AppDbContext db, NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Search products. Any combination of filters applies; all of them are
    /// backed by GIN indexes (see <see cref="AppDbContext"/>).
    /// </summary>
    public async Task<List<Product>> SearchAsync(
        string? search, string? tag, string? specKey, string? specValue,
        int page, int pageSize, CancellationToken ct = default)
    {
        var offset = (page - 1) * pageSize;

        // Full-text search against the trigger-maintained tsvector, ranked.
        // The user input is first normalized by SearchQuery.ToTsQuery into a
        // prefix query ("wireles" -> "wireles:*"), then passed as a
        // parameter — never concatenated into the SQL. Prefix matching is
        // why this uses to_tsquery instead of websearch_to_tsquery, which
        // has no prefix support (see docs/jsonb-and-full-text-search.md).
        var tsQuery = SearchQuery.ToTsQuery(search);
        if (tsQuery.Length > 0)
        {
            return await db.Products
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM "Products"
                    WHERE "SearchVector" @@ to_tsquery('english', {tsQuery})
                    ORDER BY ts_rank("SearchVector", to_tsquery('english', {tsQuery})) DESC
                    LIMIT {pageSize} OFFSET {offset}
                    """)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        // Array containment: does the product carry this tag?
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var normalized = SearchQuery.NormalizeTag(tag);
            return await db.Products
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM "Products"
                    WHERE "Tags" @> ARRAY[{normalized}]::text[]
                    ORDER BY "Name" LIMIT {pageSize} OFFSET {offset}
                    """)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        // JSONB: match one key/value inside Specs, e.g. color=black.
        if (!string.IsNullOrWhiteSpace(specKey) && specValue is not null)
        {
            return await db.Products
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM "Products"
                    WHERE "Specs" ->> {specKey} = {specValue}
                    ORDER BY "Name" LIMIT {pageSize} OFFSET {offset}
                    """)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        return await db.Products
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public Task<Product?> GetByIdAsync(int id, CancellationToken ct = default) =>
        db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>Reads one JSONB value straight out of Specs without loading the row.</summary>
    public async Task<string?> GetSpecValueAsync(int id, string key, CancellationToken ct = default)
    {
        // SqlQueryRaw<string> expects the single result column to be named
        // "Value" — without the alias EF Core throws "column t.Value does
        // not exist" (42703).
        var result = await db.Database
            .SqlQueryRaw<string>(
                """SELECT "Specs" ->> {0} AS "Value" FROM "Products" WHERE "Id" = {1}""",
                key, id)
            .SingleOrDefaultAsync(ct);
        return result;
    }

    public async Task<Product> CreateAsync(Product product, CancellationToken ct = default)
    {
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product;
    }

    /// <summary>
    /// Same query, two data-access styles, timed. EF Core (LINQ) vs Dapper
    /// (raw SQL over the same Npgsql connection). Run it a few times and
    /// compare — the point is the shape of the difference, not one number.
    /// </summary>
    public async Task<CompareResult> CompareEfVsDapperAsync(string search, CancellationToken ct = default)
    {
        var tsQuery = SearchQuery.ToTsQuery(search);
        if (tsQuery.Length == 0)
        {
            throw new ArgumentException("Search text has no searchable words.", nameof(search));
        }

        var efTimer = Stopwatch.StartNew();
        var efRows = await db.Products
            .FromSqlInterpolated(
                $"""
                SELECT * FROM "Products"
                WHERE "SearchVector" @@ to_tsquery('english', {tsQuery})
                ORDER BY "Name"
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        efTimer.Stop();

        var dapperTimer = Stopwatch.StartNew();
        // Dapper gets its connection from the same data source EF Core uses,
        // so both sides share one pool and one set of credentials. (Do not
        // rebuild it from Database.GetConnectionString(): a data source does
        // not hand the password back out.)
        await using var connection = dataSource.CreateConnection();
        var dapperRows = (await connection.QueryAsync<ProductSummary>(
            """
            SELECT "Id", "Sku", "Name", "Price" FROM "Products"
            WHERE "SearchVector" @@ to_tsquery('english', @TsQuery)
            ORDER BY "Name"
            """,
            new { TsQuery = tsQuery })).ToList();
        dapperTimer.Stop();

        return new CompareResult(
            Search: search,
            TsQuery: tsQuery,
            EfCoreMilliseconds: efTimer.Elapsed.TotalMilliseconds,
            EfCoreRows: efRows.Count,
            DapperMilliseconds: dapperTimer.Elapsed.TotalMilliseconds,
            DapperRows: dapperRows.Count);
    }
}

public sealed record ProductSummary(int Id, string Sku, string Name, decimal Price);

public sealed record CompareResult(
    string Search,
    string TsQuery,
    double EfCoreMilliseconds,
    int EfCoreRows,
    double DapperMilliseconds,
    int DapperRows)
{
    /// <summary>Both approaches must return the same rows — if they don't, the query drifted.</summary>
    public bool RowCountsMatch => EfCoreRows == DapperRows;
}
