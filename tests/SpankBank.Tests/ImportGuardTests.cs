using SpankBank.Data.Import;

namespace SpankBank.Tests;

public class ImportGuardTests
{
    [Fact]
    public void Allows_a_normal_daily_change()
        => Assert.Null(ImportGuard.Check(existing: 6_800_000, incoming: 6_810_000, forced: false));

    [Fact]
    public void Allows_a_small_drop()
        => Assert.Null(ImportGuard.Check(6_800_000, 6_300_000, forced: false));

    [Fact]
    public void Refuses_a_drop_of_more_than_ten_percent()
        => Assert.NotNull(ImportGuard.Check(6_800_000, 6_000_000, forced: false));

    [Fact]
    public void Refuses_half_the_catalog()
        => Assert.NotNull(ImportGuard.Check(6_800_000, 3_500_000, forced: false));

    [Fact]
    public void Refuses_an_empty_export()
        => Assert.NotNull(ImportGuard.Check(6_800_000, 0, forced: false));

    [Fact]
    public void Force_overrides()
        => Assert.Null(ImportGuard.Check(6_800_000, 10, forced: true));

    [Fact]
    public void A_new_or_tiny_catalog_accepts_anything()
        => Assert.Null(ImportGuard.Check(existing: 0, incoming: 5, forced: false));
}
