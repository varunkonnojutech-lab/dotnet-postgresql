namespace PostgresDemo.Models;

/// <summary>
/// An order placed against a product. <see cref="IdempotencyKey"/> is supplied
/// by the caller (the <c>Idempotency-Key</c> header) and is unique: replaying
/// the same request returns the original order instead of charging twice.
/// The advisory-lock flow that enforces this lives in
/// <see cref="Services.OrderService"/>.
/// </summary>
public sealed class Order
{
    public int Id { get; set; }

    public required string IdempotencyKey { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int Quantity { get; set; }

    public decimal TotalAmount { get; set; }

    public string Status { get; set; } = "Created";

    public DateTime CreatedUtc { get; set; }
}
