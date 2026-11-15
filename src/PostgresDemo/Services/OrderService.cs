using Microsoft.EntityFrameworkCore;
using PostgresDemo.Data;
using PostgresDemo.Models;

namespace PostgresDemo.Services;

/// <summary>
/// Creates orders exactly once per idempotency key, even under retries.
///
/// <para>
/// The flow, all inside one transaction:
/// <list type="number">
///   <item>Take a transaction-scoped advisory lock for the key
///   (<c>pg_try_advisory_xact_lock</c>). Two concurrent requests for the
///   same key serialize here instead of racing to the INSERT.</item>
///   <item>Look up the key. Found → this is a replay: return the original order.</item>
///   <item>Not found → insert the order. The unique index on
///   <c>IdempotencyKey</c> is the backstop if anything above ever slips.</item>
/// </list>
/// The lock is released automatically when the transaction commits or rolls
/// back — nothing to clean up, and a crashed request never holds it.
/// </para>
/// </summary>
public sealed class OrderService(AppDbContext db)
{
    public async Task<OrderResult> CreateOrderAsync(
        int productId, int quantity, string idempotencyKey, CancellationToken ct = default)
    {
        var key = Idempotency.NormalizeKey(idempotencyKey);
        var lockKey = Idempotency.LockKey(key);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // If another request holds the lock for this key, it is mid-insert:
        // poll briefly for its order, then give up instead of double-charging.
        var locked = await db.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", lockKey)
            .SingleAsync(ct);

        if (!locked)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, ct);
                var raced = await db.Orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);
                if (raced is not null)
                {
                    await tx.CommitAsync(ct);
                    return new OrderResult(raced, Created: false);
                }
            }

            await tx.RollbackAsync(ct);
            return new OrderResult(null, Created: false, Conflict: true);
        }

        var existing = await db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return new OrderResult(existing, Created: false);
        }

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            await tx.RollbackAsync(ct);
            return new OrderResult(null, Created: false, NotFound: true);
        }

        var order = new Order
        {
            IdempotencyKey = key,
            ProductId = productId,
            Quantity = quantity,
            TotalAmount = product.Price * quantity,
            Status = "Created",
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new OrderResult(order, Created: true);
    }

    public Task<Order?> GetByIdAsync(int id, CancellationToken ct = default) =>
        db.Orders
            .Include(o => o.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
}

public sealed record OrderResult(Order? Order, bool Created, bool Conflict = false, bool NotFound = false);
