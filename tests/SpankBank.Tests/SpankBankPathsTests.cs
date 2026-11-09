using SpankBank.Data;

namespace SpankBank.Tests;

public class SpankBankPathsTests
{
    private static SpankBankPaths With(params (string Name, string Value)[] values)
        => new(name => values.FirstOrDefault(v => v.Name == name).Value);

    [Fact]
    public void By_default_it_is_the_two_container_mounts()
    {
        var paths = With();

        Assert.Equal("/data", paths.DataDir);
        Assert.Equal("/data/db", paths.DbDir);
        Assert.Equal("/data/imports", paths.ImportsDir);
        Assert.Equal("/data/keys", paths.KeysDir);
        Assert.Equal("/media", paths.MediaDir);
    }

    [Fact]
    public void Media_does_not_live_under_data_and_the_overrides_are_independent()
    {
        var paths = With(("SPANKBANK_DATA_DIR", "/work/data"), ("SPANKBANK_MEDIA_DIR", "/work/media"));

        Assert.Equal("/work/data/db", paths.DbDir);
        Assert.Equal("/work/media", paths.MediaDir);
        Assert.Equal("/media", With(("SPANKBANK_DATA_DIR", "/work/data")).MediaDir);
        Assert.Equal("/data", With(("SPANKBANK_MEDIA_DIR", "/work/media")).DataDir);
    }

    [Fact]
    public void Blank_overrides_count_as_unset()
        => Assert.Equal(("/data", "/media"), (With(("SPANKBANK_DATA_DIR", " ")).DataDir, With(("SPANKBANK_MEDIA_DIR", "")).MediaDir));
}
