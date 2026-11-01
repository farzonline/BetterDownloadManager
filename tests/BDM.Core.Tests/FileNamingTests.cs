using BDM.Core.Files;

namespace BDM.Core.Tests;

public class FileNamingTests
{
    [Fact] public void Sanitize_ReplacesIllegalChars() =>
        Assert.Equal("a_b__c_.mp4", FileNaming.Sanitize("a<b>:c?.mp4"));

    [Fact] public void Sanitize_PrefixesReservedNames() =>
        Assert.Equal("_CON.txt", FileNaming.Sanitize("CON.txt"));

    [Fact]
    public void Sanitize_LongName_KeepsExtensionAndCapsLength()
    {
        var r = FileNaming.Sanitize(new string('a', 300) + ".mp4");
        Assert.EndsWith(".mp4", r);
        Assert.True(r.Length <= 200);
    }

    [Fact]
    public void Unique_AppendsCounterWhenExists()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "a.zip"), "x");
        Assert.Equal("a (1).zip", FileNaming.Unique(dir, "a.zip"));
        Assert.Equal("b.zip", FileNaming.Unique(dir, "b.zip"));
    }

    [Fact] public void Resolve_ContentDispositionStar_Decodes() =>
        Assert.Equal("€.pdf", FileNaming.Resolve("attachment; filename*=UTF-8''%E2%82%AC.pdf", new Uri("http://x/y"), null));

    [Fact] public void Resolve_FromUrlAndContentType() =>
        Assert.Equal("video.mp4", FileNaming.Resolve(null, new Uri("http://x/y/video?id=3"), "video/mp4"));
}
