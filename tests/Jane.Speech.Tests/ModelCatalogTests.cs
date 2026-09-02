using Jane.Speech;

namespace Jane.Speech.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void PinsParakeetV2EnglishInt8WithSha256()
    {
        var asset = ModelCatalog.ParakeetV2Int8;

        // v2, not v3: English-only is locked, and the English-specialised v2 is both more accurate
        // on English and faster than the 25-language v3. int8, not fp32: the fp32 export is the
        // same model at three times the RAM and no better on this workload.
        Assert.Contains("parakeet-tdt-0.6b-v2", asset.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("int8", asset.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("v3", asset.Id, StringComparison.Ordinal);

        Assert.Equal(64, asset.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        Assert.True(asset.SizeBytes > 400_000_000);
    }

    [Fact]
    public void ParakeetAttributionIsShipped_BecauseCcBy40RequiresIt()
    {
        // CC-BY-4.0 makes attribution a licence obligation. The About view and NOTICE.md both
        // render this string, so an empty one is a licence violation, not a cosmetic gap.
        Assert.Equal("CC-BY-4.0", ModelCatalog.ParakeetV2Int8.License);
        Assert.Contains("NVIDIA", ModelCatalog.ParakeetV2Int8.Attribution, StringComparison.Ordinal);
        Assert.All(ModelCatalog.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.License));
            Assert.False(string.IsNullOrWhiteSpace(a.Attribution));
        });
    }

    [Fact]
    public void SileroVadIsCatalogued_SoItCanNeverBeSilentlyAbsent()
    {
        // OpenWhispr#1057 is the case where a missing VAD model silently disabled VAD instead of
        // failing. Cataloguing it is what makes its absence a typed error.
        var vad = ModelCatalog.SileroVad;

        Assert.Contains(vad, ModelCatalog.Required);
        Assert.Matches("^[0-9a-f]{64}$", vad.Sha256);
        Assert.Equal("silero_vad.onnx", vad.RelativePath);
    }

    [Fact]
    public void EveryAssetHasAPinnedHashAndAPlausibleSize()
    {
        Assert.All(ModelCatalog.All, asset =>
        {
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
            Assert.True(asset.SizeBytes > 0, $"{asset.Id} has no expected size.");
            Assert.True(asset.Url.Scheme == Uri.UriSchemeHttps, $"{asset.Id} is not fetched over HTTPS.");
        });
    }

    [Fact]
    public void AllUrlsPointAtThePinnedUpstreamHosts()
    {
        // The only outbound network Jane ever performs. Restricting it to two known hosts is what
        // makes "no network on the dictation path" checkable rather than aspirational.
        string[] allowed = ["github.com", "huggingface.co"];

        Assert.All(ModelCatalog.All, asset =>
            Assert.Contains(asset.Url.Host, allowed));
    }

    [Fact]
    public void WhisperQuantAxisReflectsWhatUpstreamActuallyShips()
    {
        // The plan names Q4_0/Q5_0/Q8_0. Upstream ships no Q4_0 build of large-v3-turbo, so the
        // axis is the two that exist -- recorded here so a reader does not think it was forgotten.
        var ids = ModelCatalog.WhisperQuants.Select(q => q.Id).ToArray();

        Assert.Contains("whisper-large-v3-turbo-q5_0", ids);
        Assert.Contains("whisper-large-v3-turbo-q8_0", ids);
        Assert.DoesNotContain(ids, id => id.Contains("q4_0", StringComparison.Ordinal));
    }

    [Fact]
    public void ByIdThrowsForAnUnknownAsset()
    {
        Assert.Throws<KeyNotFoundException>(() => ModelCatalog.ById("no-such-model"));
        Assert.Equal(ModelCatalog.ParakeetV2Int8, ModelCatalog.ById("parakeet-tdt-0.6b-v2-int8"));
    }
}
