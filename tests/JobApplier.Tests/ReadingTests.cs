using System.Net;
using JobApplier.Reading;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class ReadingTests
{
    private const string Rss = """
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>Blog</title>
          <item><title>Newer post</title><link>https://devblogs.example/new</link>
            <pubDate>Tue, 25 Nov 2026 09:00:00 GMT</pubDate>
            <description>&lt;p&gt;Hello &amp;amp; welcome, &lt;b&gt;reader&lt;/b&gt;.&lt;/p&gt;</description></item>
          <item><title>Older post</title><link>https://devblogs.example/old</link>
            <pubDate>Mon, 10 Nov 2026 09:00:00 GMT</pubDate><description>Old one.</description></item>
          <item><title>No link</title><description>skipped</description></item>
        </channel></rss>
        """;

    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry><title>Architecture note</title>
            <link rel="self" href="https://feed.example/self"/>
            <link rel="alternate" href="https://martinfowler.example/note"/>
            <updated>2026-11-20T08:00:00Z</updated><summary>A short summary.</summary></entry>
        </feed>
        """;

    private static readonly ArticleSource Source = new("Test source", "https://example.test/feed", "Test");

    [Fact]
    public void An_rss_feed_gives_its_articles_with_title_link_date_and_plain_summary()
    {
        var articles = ArticleService.Parse(Rss, Source);

        Assert.Equal(2, articles.Count);   // the item without a link is left out
        Assert.Equal("Newer post", articles[0].Title);
        Assert.Equal("https://devblogs.example/new", articles[0].Link);
        Assert.Equal("Hello & welcome, reader .", articles[0].Summary);   // tags removed, entities decoded
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), articles[0].Published);
    }

    [Fact]
    public void An_atom_feed_uses_the_alternate_link_not_the_self_link()
    {
        var article = Assert.Single(ArticleService.Parse(Atom, Source));

        Assert.Equal("https://martinfowler.example/note", article.Link);
        Assert.Equal("A short summary.", article.Summary);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), article.Published);
    }

    [Fact]
    public void Only_web_addresses_are_kept_as_links()
    {
        const string bad = """<rss><channel><item><title>x</title><link>javascript:alert(1)</link></item></channel></rss>""";

        Assert.Empty(ArticleService.Parse(bad, Source));
    }

    [Fact]
    public void A_page_that_is_not_a_feed_is_refused()
    {
        Assert.Throws<FormatException>(() => ArticleService.Parse("<html><body>Hello</body></html>", Source));
    }

    [Fact]
    public void Summaries_are_plain_text_and_short()
    {
        Assert.Equal("a b", ArticleService.Clean("<div>a</div>\n<span>b</span>", 300));
        Assert.Equal(13, ArticleService.Clean(new string('x', 50), 10).Length);   // ten characters and "..."
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("infoq", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var newer = request.RequestUri.Host.Contains("devblogs", StringComparison.OrdinalIgnoreCase) ? Rss : Atom;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(newer) });
        }
    }

    [Fact]
    public async Task one_feed_that_fails_does_not_hide_the_others_and_newest_come_first()
    {
        using var http = new HttpClient(new FeedHandler());
        var service = new ArticleService(http, NullLogger<ArticleService>.Instance);

        var result = await service.LatestAsync(refresh: true, CancellationToken.None);

        Assert.Contains(result.Failed, name => name.Contains("InfoQ"));
        Assert.True(result.Articles.Count > 0);
        Assert.Equal(result.Articles.OrderByDescending(a => a.Published).Select(a => a.Link), result.Articles.Select(a => a.Link));
    }

    [Fact]
    public async Task the_result_is_kept_for_a_while_unless_a_refresh_is_asked_for()
    {
        using var http = new HttpClient(new FeedHandler());
        var service = new ArticleService(http, NullLogger<ArticleService>.Instance);

        var first = await service.LatestAsync(refresh: true, CancellationToken.None);
        var again = await service.LatestAsync(refresh: false, CancellationToken.None);

        Assert.Same(first, again);
    }
}
