using JobApplier.Interview;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

/// <summary>Interview questions: one subject at a time, written from the resume and the job titles, saved to the Desktop.</summary>
public class InterviewController : Controller
{
    private static readonly int[] AllowedCounts = { 50, 100, 150, 200 };

    private readonly InterviewGenerator _generator;
    private readonly IHostApplicationLifetime _lifetime;

    public InterviewController(InterviewGenerator generator, IHostApplicationLifetime lifetime)
    {
        _generator = generator;
        _lifetime = lifetime;
    }

    [HttpGet]
    public IActionResult Index(string? segment, string? file) =>
        View(_generator.Page(segment, file, TempData["Message"] as string, TempData["Error"] as string));

    /// <summary>Writes a new set for the subject. Earlier sets are kept.</summary>
    [HttpPost]
    public async Task<IActionResult> Generate(string segment, int count = 100)
    {
        if (!AllowedCounts.Contains(count)) count = 100;
        try
        {
            var result = await _generator.GenerateAsync(segment, count, _lifetime.ApplicationStopping);
            TempData["Message"] = $"Saved {result.Saved} new questions to {result.Path}." + (result.Problem is null ? "" : " " + result.Problem);
            return RedirectToAction(nameof(Index), new { segment, file = Path.GetFileName(result.Path) });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (IOException ex)
        {
            TempData["Error"] = "Could not save the questions: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            TempData["Error"] = "The app was closed while the questions were being written. Nothing was saved.";
        }
        return RedirectToAction(nameof(Index), new { segment });
    }
}
