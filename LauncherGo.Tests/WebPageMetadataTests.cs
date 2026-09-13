using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class WebPageMetadataTests
{
    [Fact]
    public void HtmlIncludesEscapedMetadataAndNoDuplicates()
    {
        const string template = "<head><!-- site-metadata:start --><title>ServerMap</title><!-- site-metadata:end --></head><body>map</body>";
        var site = new WebPageMetadata { Title = "地图 </title><script>alert(1)</script>", Description = "\" onload=\"bad & text", Keywords = "地图,路线", FaviconUrl = "https://example.com/icon.png?a=1&b=2" };
        var html = site.ApplyToHtml(template);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;/title&gt;", html);
        Assert.Contains("&quot; onload=&quot;", html);
        Assert.Contains("?a=1&amp;b=2", html);
        Assert.Contains("property=\"og:title\"", html);
        Assert.EndsWith("</head><body>map</body>", html);
        Assert.Equal(html, site.ApplyToHtml(html));
    }

    [Fact]
    public void InjectsCustomCodeOnceAndEscapesClosingTags()
    {
        const string template = "<head><!-- site-metadata:start --><!-- site-metadata:end --><style>body { color: black; }</style></head><body>map</body>";
        var site = new WebPageMetadata { CustomCss = "body { color: red; } .example::after { content: '</style>'; }", CustomJs = "window.siteReady = '</script><div>'; // <!-- <script>" };
        var html = site.ApplyToHtml(template);
        Assert.Contains("content: '<\\/style>'", html);
        Assert.True(html.IndexOf("id=\"siteCustomCss\"", StringComparison.Ordinal) > html.IndexOf("color: black", StringComparison.Ordinal));
        Assert.True(html.IndexOf("id=\"siteCustomJs\"", StringComparison.Ordinal) > html.IndexOf("<body>map", StringComparison.Ordinal));
        Assert.DoesNotContain("</script><div>", html);
        Assert.Equal(1, html.Split("id=\"siteCustomCss\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, html.Split("id=\"siteCustomJs\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(html, site.ApplyToHtml(html));
        var cleared = new WebPageMetadata().ApplyToHtml(html);
        Assert.DoesNotContain("siteCustomCss", cleared);
        Assert.DoesNotContain("siteCustomJs", cleared);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml,test")]
    [InlineData("file:///C:/test.ico")]
    [InlineData("//example.com/test.ico")]
    [InlineData("https://user:password@example.com/test.ico")]
    public void RejectsUnsafeFaviconUrls(string url) => Assert.Throws<ArgumentException>(() => new WebPageMetadata { FaviconUrl = url }.Normalize());

    [Fact]
    public void EmptyValuesResetDefaultsAndLengthsAreBounded()
    {
        Assert.Equal("ServerMap", new WebPageMetadata { Title = "  " }.Normalize().Title);
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { Title = new string('x', 121) }.Normalize());
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { Description = new string('x', 501) }.Normalize());
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { Keywords = new string('x', 501) }.Normalize());
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { FaviconUrl = new string('x', 2049) }.Normalize());
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { CustomCss = new string('x', 100_001) }.Normalize());
        Assert.Throws<ArgumentException>(() => new WebPageMetadata { CustomJs = new string('x', 100_001) }.Normalize());
    }

    [Fact]
    public void PersistsMetadataAndPreservesItWhenOlderClientsSaveAnnouncements()
    {
        var directory = Path.Combine(Path.GetTempPath(), "launchergo-site-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "announcement.json");
            File.WriteAllText(path, "{\"Html\":\"old\",\"ServerWebsite\":\"https://example.com\",\"UpdatedBy\":\"admin\",\"UpdatedAt\":\"2026-09-09T00:00:00Z\"}");
            var store = new AnnouncementStore(path);
            Assert.Equal("ServerMap", store.Current.Site.Title);
            var site = new WebPageMetadata { Title = "社区地图", Description = "服务器地图", Keywords = "地图,玩家", FaviconUrl = "https://example.com/favicon.ico", CustomCss = ".map-marker { opacity: .8; }", CustomJs = "document.body.dataset.custom = '1';" };
            store.Save("news", "https://example.com", "admin", site);
            store = new AnnouncementStore(path);
            Assert.Equal(site, store.Current.Site);
            store.Save("updated", "https://example.com", "admin");
            Assert.Equal(site, new AnnouncementStore(path).Current.Site);
            Assert.Throws<ArgumentException>(() => store.Save("invalid", "https://example.com", "admin", site with { FaviconUrl = "javascript:test" }));
            Assert.Equal("updated", store.Current.Html);
            store.Save("reset", "https://example.com", "admin", new());
            Assert.Equal(new WebPageMetadata(), new AnnouncementStore(path).Current.Site);
        }
        finally { Directory.Delete(directory, true); }
    }
}
