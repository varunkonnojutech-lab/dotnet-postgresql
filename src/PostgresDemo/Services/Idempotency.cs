using System.Security.Cryptography;
using System.Text;

namespace PostgresDemo.Services;

/// <summary>
/// Idempotency-key helpers for <see cref="OrderService"/>.
/// Pure logic, no database — fully unit-testable.
///
/// <para>
/// Postgres advisory locks take a single <c>bigint</c> key, so the opaque
/// string key the client sends has to become a stable 64-bit number.
/// SHA-256, first 8 bytes, little-endian: deterministic, and collisions
/// are a non-issue at this scale (2^64 space). The lock key is derived
/// from the <em>normalized</em> key, so <c>"  Order-1 "</c> and
/// <c>"order-1"</c> lock the same slot — same as the unique index does.
/// </para>
/// </summary>
public static class Idempotency
{
    public static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim().ToLowerInvariant();
    }

    /// <summary>Deterministically maps an idempotency key to a Postgres advisory-lock key.</summary>
    public static long LockKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeKey(key)));
        return BitConverter.ToInt64(hash, 0);
    }
}
