using JobApplier.Reading;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

/// <summary>Bonus reading: the latest articles from trusted engineering feeds, filtered by source and title.</summary>
public class ReadingController : Controller
{
    private readonly ArticleService _articles;

    public ReadingController(ArticleService articles) => _articles = articles;

    [HttpGet]
    public async Task<IActionResult> Index(string? source, string? q, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var result = await _articles.LatestAsync(refresh, cancellationToken);
        var query = (q ?? "").Trim();

        IEnumerable<Article> shown = result.Articles;
        if (!string.IsNullOrEmpty(source)) shown = shown.Where(a => a.Source == source);
        if (query.Length > 0)
            shown = shown.Where(a => a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Summary.Contains(query, StringComparison.OrdinalIgnoreCase));

        var list = shown.Take(150).ToList();
        return View(new ReadingPage(list, ArticleService.Sources.Select(s => s.Name).ToList(), source ?? "", query, result.Failed, result.FetchedUtc, result.Articles.Count));
    }
}
