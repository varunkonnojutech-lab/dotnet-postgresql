# PostgreSQL in .NET 8 — JSONB, Full-Text Search, Advisory Locks, LISTEN/NOTIFY

[![CI](https://github.com/varunkonnojutech-lab/dotnet-postgresql/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/dotnet-postgresql/actions/workflows/ci.yml)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/dotnet-postgresql)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)

Most .NET developers learn databases on SQL Server — and then meet Postgres on a real project and discover it is a different animal. Postgres is not just "another relational database": JSONB columns you can query and index, full-text search with no extra service, array columns, advisory locks, and a built-in pub/sub channel. This repo is a runnable lab for all of it, built around a small product catalog API in .NET 8 with EF Core and Npgsql.

## Who this is for

- **.NET developers who know SQL Server** and keep hearing "we use Postgres" in job descriptions. Start with `docs/postgres-for-sqlserver-devs.md` — it maps what you know.
- **Backend engineers** who need schemaless attributes, catalog search, or idempotent writes without adding Elasticsearch, Redis, or a message broker.
- **Interview prep**: "How do you make an API idempotent?" and "When would you use Postgres over SQL Server?" are senior-level questions. This repo is the answer, with code.

## What you'll learn

1. **EF Core + Npgsql, properly** — the provider package, `timestamptz` mapping, case-sensitivity gotchas, and migrations that carry database functions and triggers with the schema.
2. **JSONB in practice** — schemaless product specs in a `jsonb` column, queried with `->>` and `@>`, indexed with GIN (`jsonb_path_ops`), and when *not* to use it.
3. **Full-text search without a search service** — a trigger-maintained `tsvector` column, `to_tsquery` with prefix matching, `ts_rank` ordering, and why raw user input must never reach `to_tsquery` un-sanitized.
4. **Array columns** — `text[]` tags with `@>` containment queries and a GIN index.
5. **Idempotent writes with advisory locks** — `pg_try_advisory_xact_lock` serializes concurrent retries for the same idempotency key, so a retried request returns the original order instead of charging twice. The unique index is the backstop.
6. **LISTEN/NOTIFY** — Postgres pub/sub: a trigger calls `pg_notify` on every product change, and a hosted service listens and records the events. No polling, no queue.
7. **EF Core vs Dapper, measured** — the same query through both, timed, on the same connection. See `/compare`.

## The demo

A product catalog API. Eight seeded products with varied tags and JSONB specs, so every query shape has something to find.

| Endpoint | What it shows |
|---|---|
| `GET /products?search=wireles` | Full-text search, prefix matching, ranked by `ts_rank` |
| `GET /products?tag=usb-c` | Array containment (`Tags @> ARRAY['usb-c']`) |
| `GET /products?specKey=color&specValue=black` | JSONB lookup (`Specs ->> 'color'`) |
| `GET /products/{id}/specs/{key}` | One JSONB value without loading the row |
| `POST /products` | Insert — the trigger rebuilds the search vector and fires a NOTIFY |
| `POST /orders` + `Idempotency-Key` header | Idempotent order creation via advisory lock |
| `GET /compare?search=wireless` | EF Core vs Dapper on the same query, timed |
| `GET /notifications` | Recent LISTEN/NOTIFY events |
| `GET /health` | Database connectivity check |

## Quick start

### Option A — everything in Docker

```bash
docker compose up --build
```

That starts Postgres 16, waits for its health check, builds the API, applies migrations (including the triggers), and seeds sample data.

- Swagger: <http://localhost:8080/swagger>
- Health: <http://localhost:8080/health>
- Search: <http://localhost:8080/products?search=wireless>
- Tag filter: <http://localhost:8080/products?tag=usb-c>
- JSONB filter: <http://localhost:8080/products?specKey=color&specValue=black>

### Option B — Postgres in Docker, .NET on your machine

```bash
docker compose up -d postgres

# Apply migrations and run
dotnet ef database update --project src/PostgresDemo
dotnet run --project src/PostgresDemo
```

The default local connection targets port **5432** with the demo-only password from `docker-compose.yml`. Override it with `ConnectionStrings__PostgresDb`. Never reuse the local password outside this lab.

Try the idempotency demo with the included `PostgresDemo.http` file, or:

```bash
# First call creates the order (201)
curl -X POST http://localhost:8080/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: order-2026-11-15-001" \
  -d '{"productId": 1, "quantity": 2}'

# Replay with the same key returns the SAME order (200, no second charge)
curl -X POST http://localhost:8080/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: order-2026-11-15-001" \
  -d '{"productId": 1, "quantity": 2}'
```

## Tests

30 tests across three suites:

- **Unit tests** (`SearchQueryTests`, `IdempotencyTests`) — pure logic, no database: query building, sanitization, lock-key derivation.
- **Integration tests** (`PostgresIntegrationTests`) — 9 tests against a real Postgres: full-text search, prefix matching, tag and JSONB filters, idempotent orders (including 8 concurrent requests with the same key → exactly one order), EF vs Dapper row-count parity, and trigger-maintained search vectors.

```bash
dotnet test
```

The integration tests need Postgres: `docker compose up -d postgres` first, or set `POSTGRES_TEST_CONNECTION_STRING`. In CI they run against a `postgres:16` service container.

## Docs

- [`docs/postgres-for-sqlserver-devs.md`](docs/postgres-for-sqlserver-devs.md) — type mapping, identity columns, dates, case sensitivity, and when to pick which database.
- [`docs/jsonb-and-full-text-search.md`](docs/jsonb-and-full-text-search.md) — JSONB operators and indexing, the `tsvector` trigger pattern, `to_tsquery` vs `websearch_to_tsquery`, and when Postgres FTS is enough.

## Why this repo exists

PostgreSQL is on the resume now, and the ladder had no repo for it. SQL Server is covered in depth by `sqlserver-efcore-pro` — this is its Postgres counterpart, and the two docs cross-reference each other. If you work in .NET long enough, you will meet both.

## Next steps

- Add `pg_trgm` for typo-tolerant search (`%` similarity operator) and compare it with the `tsvector` approach.
- Move the search-vector trigger to a generated column (`GENERATED ALWAYS AS`) and measure the difference.
- Swap the in-memory notification store for a SignalR hub and push product changes to browsers live — see `signalr-realtime`.
- Try the advisory-lock pattern with `pg_advisory_lock` (session-scoped) and compare failure modes.
