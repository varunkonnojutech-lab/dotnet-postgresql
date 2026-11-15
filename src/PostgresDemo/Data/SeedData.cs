using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PostgresDemo.Models;

namespace PostgresDemo.Data;

/// <summary>Eight products with varied tags and JSONB specs, so every query shape in the API has something to find.</summary>
public static class SeedData
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct))
        {
            return;
        }

        var products = new[]
        {
            NewProduct("KB-100", "Mechanical Keyboard", "Hot-swappable mechanical keyboard with PBT keycaps and per-key RGB.", 129.99m, 42, "Accessories",
                ["keyboard", "mechanical", "rgb"],
                ("switch", "tactile"), ("layout", "TKL"), ("connectivity", "bluetooth")),
            NewProduct("MN-200", "Ultrawide Monitor", "34-inch curved ultrawide monitor, 144Hz, USB-C with 90W power delivery.", 549.00m, 18, "Displays",
                ["monitor", "ultrawide", "usb-c"],
                ("size_inches", "34"), ("refresh_hz", "144"), ("panel", "IPS")),
            NewProduct("HP-300", "Wireless Headphones", "Noise-cancelling wireless headphones with 40-hour battery life.", 199.99m, 63, "Audio",
                ["headphones", "wireless", "noise-cancelling"],
                ("battery_hours", "40"), ("bluetooth", "5.3"), ("color", "black")),
            NewProduct("MS-400", "Ergonomic Mouse", "Vertical ergonomic mouse with silent switches and a 4000 DPI sensor.", 79.50m, 120, "Accessories",
                ["mouse", "ergonomic", "wireless"],
                ("dpi", "4000"), ("hand", "right"), ("connectivity", "bluetooth")),
            NewProduct("DK-500", "USB-C Docking Station", "11-in-1 USB-C dock: dual 4K HDMI, gigabit ethernet, 100W charging.", 179.00m, 27, "Accessories",
                ["dock", "usb-c", "dual-monitor"],
                ("ports", "11"), ("power_watts", "100"), ("video", "dual-4k")),
            NewProduct("WB-600", "Webcam 4K", "4K webcam with autofocus, dual noise-cancelling microphones, and a privacy shutter.", 149.99m, 35, "Video",
                ["webcam", "4k", "autofocus"],
                ("resolution", "4k"), ("microphone", "dual"), ("privacy_shutter", "true")),
            NewProduct("SP-700", "Portable SSD 1TB", "Rugged portable SSD, 1TB, USB-C 10Gbps, IP55 splash resistance.", 109.99m, 88, "Storage",
                ["ssd", "portable", "usb-c"],
                ("capacity", "1TB"), ("speed_mbps", "1050"), ("rugged", "true")),
            NewProduct("LP-800", "Laptop Stand", "Adjustable aluminium laptop stand, holds 11 to 17 inch laptops, folds flat.", 49.99m, 200, "Accessories",
                ["stand", "laptop", "aluminium"],
                ("max_inches", "17"), ("material", "aluminium"), ("foldable", "true")),
        };

        db.Products.AddRange(products);
        await db.SaveChangesAsync(ct);
    }

    private static Product NewProduct(
        string sku, string name, string description, decimal price, int stock, string category,
        string[] tags, params (string Key, string Value)[] specs)
    {
        var specDict = specs.ToDictionary(
            s => s.Key,
            s => JsonSerializer.Deserialize<JsonElement>($"\"{s.Value}\""));

        return new Product
        {
            Sku = sku,
            Name = name,
            Description = description,
            Price = price,
            Stock = stock,
            Category = category,
            Tags = tags,
            Specs = specDict,
        };
    }
}
