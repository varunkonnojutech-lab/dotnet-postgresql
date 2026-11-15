# JSONB and Full-Text Search in Postgres

Two Postgres features that replace entire services in small-to-medium apps: schemaless JSON storage you can query and index, and real full-text search without standing up Elasticsearch.

## JSONB: schemaless, but not lawless

`jsonb` stores JSON in a decomposed binary format — keys are deduplicated, values are typed, and (unlike `json`) it supports indexing. This repo stores product specs there: every product has different attributes, and a rigid table would mean either dozens of nullable columns or an EAV mess.

```sql
-- Specs column for the headphones:
{"battery_hours": "40", "bluetooth": "5.3", "color": "black"}
```

### The operators that matter

| Operator | Meaning | Example |
|---|---|---|
| `->` | Get key as JSON | `Specs -> 'color'` → `"black"` (JSON) |
| `->>` | Get key as text | `Specs ->> 'color'` → `black` (text) |
| `@>` | Contains | `Specs @> '{"color": "black"}'` |
| `?` | Has key | `Specs ? 'color'` |
| `?\|` | Has any key | `Specs ?\| array['color','size']` |
| `#>>` | Get nested path as text | `Specs #>> '{dimensions,width}'` |

The API in this repo uses `->>` (single-value lookup) and could use `@>` for multi-key matching. Both are in the tests.

### Indexing JSONB

A plain B-tree index on a `jsonb` column is nearly useless. Use GIN:

```sql
-- Good for @> (contains) queries — the common case:
CREATE INDEX ON "Products" USING GIN ("Specs" jsonb_path_ops);

-- Good for general-purpose queries (? , @>, ->, etc.), larger index:
CREATE INDEX ON "Products" USING GIN ("Specs");
```

`jsonb_path_ops` is smaller and faster but only accelerates `@>`. This repo uses `jsonb_path_ops` because containment is the query shape it needs — the choice is documented in `AppDbContext`, not hidden.

### JSONB rules of thumb

- **Do** use JSONB for attributes that vary per row, are read as a unit, and are never joined on.
- **Do** keep values you filter or sort on frequently in real columns — an index on `(Specs ->> 'color')` (an expression index) works, but a `Color` column is clearer.
- **Don't** store data you need foreign keys, uniqueness, or cross-row aggregation on.
- **Don't** nest deeply. One level of keys is a sweet spot; three levels is a design smell.

### EF Core mapping

```csharp
public Dictionary<string, JsonElement> Specs { get; set; } = new();
// in OnModelCreating:
entity.Property(p => p.Specs).HasColumnType("jsonb");
```

The Npgsql provider serializes the dictionary to JSONB. `JsonElement` values keep numbers and booleans typed. For fixed-shape JSON, map a POCO instead — you get compile-time safety for free.

> **Npgsql 8 gotcha:** dynamic JSON serialization is opt-in since Npgsql 8.0. A `Dictionary<string, JsonElement>` mapped to `jsonb` throws `NotSupportedException` ("required dynamic JSON serialization") unless the data source opts in. This repo does it in `Program.cs` with a `NpgsqlDataSourceBuilder` (`EnableDynamicJson()`, then `UseNpgsql(dataSource)`) — and the test fixture does the same, because tests build their own contexts. If you ever see that exception, this is the line you forgot.

## Full-text search: `tsvector` and `tsquery`

Full-text search in Postgres has two sides:

- **`tsvector`**: the document, tokenized and stemmed. `"Noise-cancelling wireless headphones"` becomes `'cancell':2 'headphon':4 'noise':1 'wireless':3` — note the stemming (`headphones` → `headphon`, `cancelling` → `cancell`) and the position numbers.
- **`tsquery`**: the query. `'wireless' & 'headphon'` matches documents containing both lexemes.

### The trigger pattern (used in this repo)

Keeping the `tsvector` column in sync by hand is a bug waiting to happen. A trigger does it:

```sql
CREATE OR REPLACE FUNCTION products_search_vector_update() RETURNS trigger AS $$
BEGIN
    NEW."SearchVector" := to_tsvector('english',
        coalesce(NEW."Name", '') || ' ' || coalesce(NEW."Description", ''));
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

CREATE TRIGGER products_search_vector_trigger
    BEFORE INSERT OR UPDATE OF "Name", "Description" ON "Products"
    FOR EACH ROW EXECUTE FUNCTION products_search_vector_update();
```

The trigger is created by the EF Core migration, so it travels with the schema. `coalesce` matters: `to_tsvector` of a NULL input is NULL, and a NULL `tsvector` never matches anything.

### `to_tsquery` vs `websearch_to_tsquery` vs `plainto_tsquery`

| Function | Input | Best for |
|---|---|---|
| `to_tsquery` | Query language (`&`, `\|`, `!`, `:*`) | Programmatic queries — you build the string |
| `websearch_to_tsquery` | Google-style text (`"exact phrase" -excluded`) | Passing raw user input straight through |
| `plainto_tsquery` | Plain words | AND-ing every word, no operators |

This repo uses `to_tsquery` with a query built by `SearchQuery.ToTsQuery`: every word becomes a **prefix** term (`wireless:*`), so partial words match. User input is sanitized first — raw input must never reach `to_tsquery`, because `& | ! : * ( )` are operators there and a user typing `C++` would either error or mean something unintended.

The stemming gotcha with prefix queries: `to_tsquery` stems each word **before** applying `:*`, so `'cancelling:*'` becomes `'cancell:*'` and matches the stemmed document lexeme. That is why prefix search works here — and why you should test it (the integration tests do).

### Ranking

`ts_rank(vector, query)` scores matches: more occurrences and earlier positions rank higher. This repo orders search results by rank, so the best match is first. `ts_rank_cd` (cover density) is an alternative that rewards words appearing close together.

### Indexing

```sql
CREATE INDEX ON "Products" USING GIN ("SearchVector");
```

Without the GIN index, every search scans the whole table and re-parses the document. With it, search is an index lookup. The index is created by the migration alongside the column.

### When Postgres FTS is enough — and when it isn't

**Enough:** catalog search, document search, admin search boxes, anything where "words in this row" is the query shape.

**Not enough:** fuzzy matching across languages, typo tolerance (`pg_trgm` helps a little), faceted search at scale, relevance tuning beyond `ts_rank`, or searching across services. That is when Elasticsearch / OpenSearch / Azure AI Search earns its keep.

## Try it

```bash
# Search (prefix matching — "wireles" finds "wireless")
curl "http://localhost:8080/products?search=wireles"

# Tag filter (array containment)
curl "http://localhost:8080/products?tag=usb-c"

# JSONB spec filter
curl "http://localhost:8080/products?specKey=color&specValue=black"

# One JSONB value, no row load
curl "http://localhost:8080/products/3/specs/battery_hours"

# EF Core vs Dapper on the same query, timed
curl "http://localhost:8080/compare?search=wireless"
```
