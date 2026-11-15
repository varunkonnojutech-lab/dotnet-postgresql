using Microsoft.EntityFrameworkCore;
using PostgresDemo.Models;

namespace PostgresDemo.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.Property(p => p.Sku).HasMaxLength(32);
            entity.Property(p => p.Name).HasMaxLength(200);
            entity.Property(p => p.Description).HasMaxLength(2000);
            entity.Property(p => p.Price).HasColumnType("numeric(10,2)");
            entity.Property(p => p.Category).HasMaxLength(100);

            // Postgres-native columns. These are the whole point of the repo:
            // arrays, jsonb, and a trigger-maintained tsvector.
            entity.Property(p => p.Tags).HasColumnType("text[]");
            entity.Property(p => p.Specs).HasColumnType("jsonb");
            entity.Property(p => p.SearchVector).HasColumnType("tsvector");

            // GIN indexes make the array / jsonb / full-text queries fast.
            // jsonb_path_ops is the smaller, faster opclass when you only
            // ever query with @> (contains) — which is the common case.
            entity.HasIndex(p => p.SearchVector).HasMethod("GIN");
            entity.HasIndex(p => p.Tags).HasMethod("GIN");
            entity.HasIndex(p => p.Specs).HasMethod("GIN").HasOperators("jsonb_path_ops");

            entity.Property(p => p.CreatedUtc).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.HasIndex(o => o.IdempotencyKey).IsUnique();
            entity.Property(o => o.IdempotencyKey).HasMaxLength(128);
            entity.Property(o => o.TotalAmount).HasColumnType("numeric(10,2)");
            entity.Property(o => o.Status).HasMaxLength(32);
            entity.Property(o => o.CreatedUtc).HasDefaultValueSql("now()");

            entity.HasOne(o => o.Product)
                .WithMany()
                .HasForeignKey(o => o.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
