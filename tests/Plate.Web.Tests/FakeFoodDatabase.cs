using Plate.Core.Nutrition;

namespace Plate.Web.Tests;

/// <summary>Stands in for Open Food Facts so tests never call the real API.</summary>
public sealed class FakeFoodDatabase : IFoodDatabase
{
    public Dictionary<string, ProductLookup> Products { get; } = [];

    public int Lookups { get; private set; }

    public Task<ProductLookup> GetByBarcodeAsync(string barcode, CancellationToken ct)
    {
        Lookups++;
        return Task.FromResult(Products.GetValueOrDefault(barcode) ?? ProductLookup.Of(LookupStatus.NotFound));
    }

    public Task<ProductSearch> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult(new ProductSearch(LookupStatus.Found, []));
}
