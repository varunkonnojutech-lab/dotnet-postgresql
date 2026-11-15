using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace PostgresDemo.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Sku = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Stock = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tags = table.Column<string[]>(type: "text[]", nullable: false),
                    Specs = table.Column<Dictionary<string, JsonElement>>(type: "jsonb", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Orders_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IdempotencyKey",
                table: "Orders",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ProductId",
                table: "Orders",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SearchVector",
                table: "Products",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Specs",
                table: "Products",
                column: "Specs")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_Tags",
                table: "Products",
                column: "Tags")
                .Annotation("Npgsql:IndexMethod", "GIN");

            // Keep the tsvector in sync: every insert/update rebuilds it from
            // Name + Description. The column is nullable in the model so
            // inserts can leave it NULL — this trigger fills it first.
            migrationBuilder.Sql(
                """
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
                """);

            // Postgres pub/sub: every product change notifies the
            // 'product_events' channel, which PostgresListener subscribes to.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION products_notify() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('product_events',
                        json_build_object('event', TG_OP, 'id', NEW."Id", 'sku', NEW."Sku", 'name', NEW."Name")::text);
                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER products_notify_trigger
                    AFTER INSERT OR UPDATE ON "Products"
                    FOR EACH ROW EXECUTE FUNCTION products_notify();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS products_notify_trigger ON "Products";
                DROP FUNCTION IF EXISTS products_notify();
                DROP TRIGGER IF EXISTS products_search_vector_trigger ON "Products";
                DROP FUNCTION IF EXISTS products_search_vector_update();
                """);

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
