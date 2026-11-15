using PostgresDemo.Services;

namespace PostgresDemo.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public void LockKey_IsDeterministic()
    {
        Assert.Equal(Idempotency.LockKey("order-1"), Idempotency.LockKey("order-1"));
    }

    [Fact]
    public void LockKey_DiffersAcrossKeys()
    {
        Assert.NotEqual(Idempotency.LockKey("order-1"), Idempotency.LockKey("order-2"));
    }

    [Fact]
    public void LockKey_IgnoresCaseAndWhitespace()
    {
        // The unique index on the normalized key and the advisory lock must
        // agree, or the same logical key could lock two different slots.
        Assert.Equal(Idempotency.LockKey("  Order-1 "), Idempotency.LockKey("order-1"));
    }

    [Fact]
    public void NormalizeKey_TrimsAndLowercases()
    {
        Assert.Equal("order-1", Idempotency.NormalizeKey("  ORDER-1 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeKey_RejectsBlankKeys(string? key)
    {
        // ThrowsAny, not Throws: ThrowIfNullOrWhiteSpace throws the
        // ArgumentNullException subclass for null, plain ArgumentException
        // for empty/whitespace — both are argument errors.
        Assert.ThrowsAny<ArgumentException>(() => Idempotency.NormalizeKey(key!));
    }
}
