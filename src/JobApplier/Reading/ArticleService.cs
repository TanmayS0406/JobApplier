using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JobApplier.Reading;

/// <summary>A public RSS or Atom feed of a reliable publisher.</summary>
public sealed record ArticleSource(string Name, string Url, string Topic);

public sealed record Article(string Source, string Title, string Link, DateTimeOffset Published, string Summary);

public sealed record ReadingResult(IReadOnlyList<Article> Articles, IReadOnlyList<string> Failed, DateTime FetchedUtc);

public sealed record ReadingPage(IReadOnlyList<Article> Articles, IReadOnlyList<string> Sources, string Source, string Query,
                                 IReadOnlyList<string> Failed, DateTime FetchedUtc, int Total);

/// <summary>
/// Latest articles from public feeds of Microsoft and other trusted engineering publishers. Only the feed is read: the title, link, date and a
/// short summary. Results are kept for half an hour; a feed that does not answer is listed as failed and the others still show.
/// </summary>
public sealed class ArticleService
{
    public static readonly IReadOnlyList<ArticleSource> Sources = new[]
    {
        new ArticleSource(".NET blog (Microsoft)", "https://devblogs.microsoft.com/dotnet/feed/", ".NET and ASP.NET Core"),
        new ArticleSource("Visual Studio blog (Microsoft)", "https://devblogs.microsoft.com/visualstudio/feed/", "Tools"),
        new ArticleSource("Azure blog (Microsoft)", "https://azure.microsoft.com/en-us/blog/feed/", "Cloud and architecture"),
        new ArticleSource("Microsoft Research", "https://www.microsoft.com/en-us/research/feed/", "AI and research"),
        new ArticleSource("Microsoft Source (news)", "https://news.microsoft.com/source/feed/", "Company news"),
        new ArticleSource("Martin Fowler", "https://martinfowler.com/feed.atom", "Architecture"),
        new ArticleSource("InfoQ", "https://feed.infoq.com/", "Engineering")
    };

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const int MaxArticles = 300;

    private readonly HttpClient _http;
    private readonly ILogger<ArticleService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReadingResult? _cache;

    public ArticleService(ILogger<ArticleService> logger) : this(CreateClient(), logger) { }

    internal ArticleService(HttpClient http, ILogger<ArticleService> logger)
    {
        _http = http;
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JobApplier/1.0");
        return client;
    }

    public async Task<ReadingResult> LatestAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache is { } fresh && DateTime.UtcNow - fresh.FetchedUtc < Lifetime) return fresh;

            var results = await Task.WhenAll(Sources.Select(s => FetchAsync(s, cancellationToken)));
            var articles = results.SelectMany(r => r.Articles)
                .GroupBy(a => a.Link).Select(g => g.First())
                .OrderByDescending(a => a.Published)
                .Take(MaxArticles).ToList();
            var failed = results.Where(r => r.Error != null).Select(r => r.Source.Name).ToList();

            // If every feed failed, keep what was shown before rather than an empty page.
            if (articles.Count == 0 && _cache != null) return _cache;
            _cache = new ReadingResult(articles, failed, DateTime.UtcNow);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(ArticleSource Source, IReadOnlyList<Article> Articles, string? Error)> FetchAsync(ArticleSource source, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await _http.GetStringAsync(source.Url, cancellationToken);
            return (source, Parse(xml, source), null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not read the feed {Feed}: {Message}", source.Name, ex.Message);
            return (source, Array.Empty<Article>(), ex.Message);
        }
    }

    /// <summary>Reads an RSS 2.0 or Atom feed. Throws when the text is not a feed at all.</summary>
    internal static IReadOnlyList<Article> Parse(string xml, ArticleSource source)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("The feed is empty.");
        var items = root.Descendants().Where(e => e.Name.LocalName is "item" or "entry").ToList();
        if (items.Count == 0 && root.Name.LocalName is not ("rss" or "feed" or "RDF"))
            throw new FormatException("The address does not give an RSS or Atom feed.");

        return items.Select(i => ToArticle(i, source)).OfType<Article>().ToList();
    }

    private static Article? ToArticle(XElement item, ArticleSource source)
    {
        var title = Clean(Child(item, "title"), 200);
        var link = Link(item);
        if (title.Length == 0 || link == null) return null;

        var date = Child(item, "pubDate") ?? Child(item, "updated") ?? Child(item, "published") ?? Child(item, "date");
        var summary = Clean(Child(item, "description") ?? Child(item, "summary") ?? Child(item, "content"), 300);
        return new Article(source.Name, title, link, ParseDate(date), summary);
    }

    private static string? Child(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>The article's own page: an Atom alternate link, or the RSS link text. Only http(s) addresses are kept.</summary>
    private static string? Link(XElement item)
    {
        var atom = item.Elements().Where(e => e.Name.LocalName == "link")
            .FirstOrDefault(e => (string?)e.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
        var text = (atom ?? Child(item, "link") ?? "").Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    /// <summary>
    /// The date of an item: RSS (RFC 822, "Tue, 25 Nov 2026 09:00:00 GMT") or ISO 8601 as Atom writes it. The weekday is ignored, because it
    /// is only repeated information. Unknown or missing dates are MinValue.
    /// </summary>
    private static DateTimeOffset ParseDate(string? text)
    {
        var value = Regex.Replace((text ?? "").Trim(), @"^[A-Za-z]{3},\s*", "");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;
    }

    /// <summary>Plain text from a summary that may hold HTML: tags removed, entities decoded, spaces tidied, cut to <paramref name="max"/>.</summary>
    internal static string Clean(string? html, int max)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "...";
    }
}
