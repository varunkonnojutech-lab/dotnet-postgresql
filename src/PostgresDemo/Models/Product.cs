using System.Text.Json;
using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace PostgresDemo.Models;

/// <summary>
/// A catalog product. The interesting columns are the Postgres-native ones:
/// <list type="bullet">
///   <item><see cref="Tags"/> maps to a <c>text[]</c> array column.</item>
///   <item><see cref="Specs"/> maps to a <c>jsonb</c> column.</item>
///   <item><see cref="SearchVector"/> is a <c>tsvector</c> column maintained by
///   a database trigger (see the initial migration) — never set it from code.</item>
/// </list>
/// </summary>
public sealed class Product
{
    public int Id { get; set; }

    public required string Sku { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public string Category { get; set; } = string.Empty;

    /// <summary>Postgres <c>text[]</c>. Query with <c>@&gt;</c> / <c>&amp;&amp;</c>.</summary>
    public string[] Tags { get; set; } = [];

    /// <summary>Postgres <c>jsonb</c>. Query with <c>-&gt;&gt;</c>, <c>@&gt;</c>, <c>?</c>.</summary>
    public Dictionary<string, JsonElement> Specs { get; set; } = new();

    /// <summary>
    /// Postgres <c>tsvector</c>, maintained by the products_search_vector_trigger.
    /// Nullable in code because inserts leave it NULL for the trigger to fill;
    /// hidden from JSON — it is a search index, not API data.
    /// </summary>
    [JsonIgnore]
    public NpgsqlTsVector? SearchVector { get; set; }

    public DateTime CreatedUtc { get; set; }
}
