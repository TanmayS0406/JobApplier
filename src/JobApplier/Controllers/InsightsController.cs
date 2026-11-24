using JobApplier.Domain;
using JobApplier.Insights;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

public sealed record InsightsViewModel(GapReport Report, int Threshold, IReadOnlyList<int> Thresholds, bool HasResume, CoachViewModel Coach);

public sealed record CoachViewModel(CoachStatus Status, int Threshold, bool HasResume, string Provider);

/// <summary>
/// "What should my resume say?": the statistical analysis of the scored jobs (ML.NET text model, Math.NET regression) and, on request, the selected AI's
/// advice on the skills it found. The resume is sent to the selected AI provider only when the user presses the button; every POST is covered
/// by the antiforgery filter.
/// </summary>
public class InsightsController : Controller
{
    public static readonly int[] Thresholds = { 60, 65, 70, 75, 80 };

    private readonly GapAnalysisService _analysis;
    private readonly ResumeCoachService _coach;
    private readonly SettingsService _settings;

    public InsightsController(GapAnalysisService analysis, ResumeCoachService coach, SettingsService settings)
    {
        _analysis = analysis;
        _coach = coach;
        _settings = settings;
    }

    [HttpGet]
    public IActionResult Index(int? t)
    {
        var threshold = Pick(t);
        return View(new InsightsViewModel(_analysis.Analyze(threshold), threshold, Thresholds, HasResume, Coach(threshold)));
    }

    /// <summary>The coach card. Polled while a run is going.</summary>
    [HttpGet]
    public IActionResult Coach(int? t)
    {
        Response.Headers.CacheControl = "no-store";
        var threshold = Pick(t);
        return PartialView("_Coach", Coach(threshold));
    }

    [HttpPost]
    public IActionResult StartCoach(int? t)
    {
        var (started, message) = _coach.Start(Pick(t));
        return Json(new { ok = started, message });
    }

    /// <summary>The coach card's data. The AI that answers is the one selected in Settings (Claude or GitHub Copilot), so the page names it.</summary>
    private CoachViewModel Coach(int threshold) =>
        new(_coach.Status(threshold), threshold, HasResume, AiProvider.Label(_settings.Current.Provider));

    private bool HasResume => !string.IsNullOrWhiteSpace(_analysis.ResumeText);

    /// <summary>The threshold asked for, or the dashboard's default minimum match, moved to the nearest one the page offers.</summary>
    private int Pick(int? asked)
    {
        var wanted = asked ?? (_settings.Current.DefaultMinMatchPercent > 0 ? _settings.Current.DefaultMinMatchPercent : 70);
        return Thresholds.OrderBy(x => Math.Abs(x - wanted)).First();
    }
}
