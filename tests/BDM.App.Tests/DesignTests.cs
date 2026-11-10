using System.Text.RegularExpressions;
using System.Xml.Linq;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.App.Tests;

/// <summary>
/// The look of the app is XAML, which only fails when it is shown. These tests catch the failures that would otherwise
/// appear at runtime: a missing resource key, a dark theme and a light theme that drifted apart, a category without an icon,
/// two windows that look the same.
/// </summary>
public class DesignTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string AppDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "BDM.App"))) d = d.Parent;
        return Path.Combine(d!.FullName, "src", "BDM.App");
    }

    static HashSet<string> Keys(string file) =>
        XDocument.Load(Path.Combine(AppDir(), file)).Descendants().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k != null).Select(k => k!).ToHashSet();

    static IEnumerable<string> XamlFiles() =>
        Directory.GetFiles(Path.Combine(AppDir(), "Views"), "*.xaml").Concat([Path.Combine(AppDir(), "Themes", "Controls.xaml"), Path.Combine(AppDir(), "Themes", "Brand.xaml"), Path.Combine(AppDir(), "Themes", "Art.xaml")]);

    [Fact]
    public void TheExeCarriesTheBrandIcon_SoTheTaskbarAndShortcutsShowIt()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "BDM.App.exe");
        using var shown = System.Drawing.Icon.ExtractAssociatedIcon(exe)!;
        using var bmp = shown.ToBitmap();
        // The brand mark is a logo-green glyph on a near-black tile; Windows' default exe icon has neither.
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height * 100 / 256); // on the arrow's shaft
        Assert.True(c.G > 180 && c.R < 120 && c.B < 160, $"centre pixel {c} is not logo green");
    }

    [Fact]
    public void WindowAndTrayIcons_ComeFromTheBrandIcon()
    {
        var img = AppIcon.Image(64);
        Assert.Equal(64, img.PixelWidth);
        using var tray = AppIcon.TrayIcon();
        Assert.Equal(32, tray.Width);
    }

    [Fact]
    public void EachDownload_ShowsTheIconOfTheAppThatOpensIt_AsInIdm()
    {
        var pdf = FileIcons.For("report.pdf");
        Assert.Equal(16, pdf.PixelWidth);
        Assert.True(pdf.IsFrozen); // shared across rows and threads
        Assert.Same(pdf, FileIcons.For("other.PDF")); // one lookup per file type
        Assert.NotSame(pdf, FileIcons.For("setup.zip"));
        Assert.Equal(16, FileIcons.For("no-extension").PixelWidth); // Windows' plain file icon
        Assert.Equal(16, FileIcons.For("x.bdm-unknown-type").PixelWidth);
    }

    [Fact]
    public void DarkAndLightThemes_DefineExactlyTheSameKeys()
    {
        var dark = Keys("Themes/Nightfall.xaml");
        var light = Keys("Themes/Daybreak.xaml");
        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
    }

    [Fact]
    public void EveryResourceKeyUsedInXaml_IsDefinedSomewhere()
    {
        var known = Keys("Themes/Nightfall.xaml").Concat(Keys("Themes/Brand.xaml")).Concat(Keys("Themes/Art.xaml")).Concat(Keys("Themes/Controls.xaml")).ToHashSet();
        var missing = new List<string>();
        foreach (var file in XamlFiles())
        {
            var text = File.ReadAllText(file);
            var local = Keys(Path.GetRelativePath(AppDir(), file)); // keys declared inside the same file
            foreach (Match m in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([\w.]+)\}"))
                if (!known.Contains(m.Groups[1].Value) && !local.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        }
        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void EveryCategory_HasAnIconAndAColourThatExistInTheBrandPalette()
    {
        var brand = Keys("Themes/Brand.xaml");
        foreach (var c in Categories.Sidebar)
        {
            Assert.False(string.IsNullOrEmpty(Categories.Glyph(c)), $"{c} has no icon");
            var hue = Categories.Hue(c);
            foreach (var suffix in new[] { "", ".Brush", ".Tint" })
                Assert.True(brand.Contains($"Hue.{hue}{suffix}"), $"{c}: Hue.{hue}{suffix} is not in Brand.xaml");
        }
        Assert.True(Categories.Sidebar.Select(Categories.Glyph).Distinct().Count() >= Categories.Sidebar.Count - 1, "categories should be told apart by icon");
    }

    [Fact]
    public void EveryWindowWithAHeader_HasItsOwnIconAndColour()
    {
        var seen = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppDir(), "Views"), "*.xaml"))
        {
            var m = Regex.Match(File.ReadAllText(file), @"<v:PageHeader[^>]*?Hue=""\{StaticResource (Hue\.\w+)\}""[^>]*?Glyph=""([^""]+)""|<v:PageHeader[^>]*?Glyph=""([^""]+)""[^>]*?Hue=""\{StaticResource (Hue\.\w+)\}""", RegexOptions.Singleline);
            if (!m.Success) continue;
            var identity = m.Groups[1].Success ? m.Groups[1].Value + m.Groups[2].Value : m.Groups[4].Value + m.Groups[3].Value;
            Assert.False(seen.TryGetValue(identity, out var other), $"{Path.GetFileName(file)} looks exactly like {other}");
            seen[identity] = Path.GetFileName(file);
        }
        Assert.True(seen.Count >= 10, $"expected every dialog to have a header, found {seen.Count}");
    }

    static DownloadItemViewModel Item(DownloadState state, DownloadFailure? failure = null, string url = "https://x.example/clip.mp4")
    {
        var req = new DownloadRequest(new Uri(url), null, null, new Dictionary<string, string>(), null, null, null, ".");
        var rec = new DownloadRecord(Guid.NewGuid(), req, state, 0, null, null, "main", DateTime.UtcNow, Failure: failure);
        return new DownloadItemViewModel(rec, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(DownloadState.Queued, "queued")]
    [InlineData(DownloadState.Running, "running")]
    [InlineData(DownloadState.Paused, "paused")]
    [InlineData(DownloadState.Completed, "done")]
    [InlineData(DownloadState.Failed, "failed")]
    public void EveryState_HasAnIconAndAKindTheRowCanBeColouredBy(DownloadState state, string kind)
    {
        var item = Item(state);
        Assert.Equal(kind, item.StateKind);
        Assert.False(string.IsNullOrEmpty(item.StateGlyph));
    }

    [Fact]
    public void StatesAreToldApartByIcon_NotByColourAlone()
    {
        var glyphs = new[] { DownloadState.Queued, DownloadState.Running, DownloadState.Paused, DownloadState.Completed, DownloadState.Failed }.Select(s => Item(s).StateGlyph);
        Assert.Equal(5, glyphs.Distinct().Count());
    }

    [Fact]
    public void ProtectedDownloads_AreShownAsProtected_NotAsAnOrdinaryFailure()
    {
        Assert.Equal("protected", Item(DownloadState.Failed, DownloadFailure.Protected).StateKind);
    }

    [Fact]
    public void PercentText_ShowsWholePercentOrADashWhenTheSizeIsUnknown()
    {
        Assert.Equal("100%", Item(DownloadState.Completed).PercentText);
        Assert.Equal("0%", Item(DownloadState.Queued).PercentText);
        Assert.Equal("—", Item(DownloadState.Running).PercentText); // running, no total yet
    }

    [Fact]
    public void RowIcon_FollowsTheFileType()
    {
        var video = Item(DownloadState.Queued, url: "https://x.example/clip.mp4");
        var zip = Item(DownloadState.Queued, url: "https://x.example/a.zip");
        Assert.Equal("Video", video.Category);
        Assert.Equal(Categories.Glyph("Video"), video.CategoryGlyph);
        Assert.Equal(Categories.Hue("Compressed"), zip.CategoryHue);
        Assert.NotEqual(video.CategoryGlyph, zip.CategoryGlyph);
    }
}
