using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

public class ProfileController : Controller
{
    private const long MaxResumeBytes = 10L * 1024 * 1024;

    private readonly ProfileService _profile;
    private readonly ProfileImporter _importer;

    public ProfileController(ProfileService profile, ProfileImporter importer)
    {
        _profile = profile;
        _importer = importer;
    }

    public IActionResult Index()
    {
        var current = _profile.Current;
        return View(new ProfileViewModel
        {
            Profile = current,
            SkillsText = string.Join(", ", current.Skills),
            HasResume = _profile.HasResume
        });
    }

    /// <summary>The stored resume exactly as it is uploaded to employers, so you can check it is the right file.</summary>
    [HttpGet]
    public IActionResult Resume()
    {
        if (!_profile.HasResume) return NotFound();

        var path = _profile.ResumePath;
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "text/plain; charset=utf-8"
        };
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, type);
    }

    /// <summary>Uploads the resume and lets Claude read it into the profile (name, e-mail, skills, full text).</summary>
    [HttpPost]
    [RequestSizeLimit(MaxResumeBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadResume(IFormFile? resume, CancellationToken cancellationToken)
    {
        if (resume == null || resume.Length == 0)
        {
            TempData["Error"] = "Choose your resume file.";
            return RedirectToAction(nameof(Index));
        }

        var extension = Path.GetExtension(resume.FileName).ToLowerInvariant();
        if (!ProfileImporter.SupportedExtensions.Contains(extension))
        {
            TempData["Error"] = "Supported resume formats: " + string.Join(", ", ProfileImporter.SupportedExtensions) + ".";
            return RedirectToAction(nameof(Index));
        }
        if (resume.Length > MaxResumeBytes)
        {
            TempData["Error"] = "The resume is larger than 10 MB.";
            return RedirectToAction(nameof(Index));
        }

        string path;
        await using (var stream = resume.OpenReadStream())
            path = await _profile.StoreResumeAsync(stream, resume.FileName, cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            var profile = await _importer.ImportAsync(path, timeout.Token);

            // The file name is only recorded by the importer on success; make sure it is set either way.
            TempData["Message"] = string.IsNullOrWhiteSpace(profile.Email)
                ? "Resume read. No e-mail address was found on it - please type it in below; it is used for every account."
                : $"Resume read. Accounts will be created with {profile.Email} - check the details below.";
        }
        catch (ClaudeAgentException ex)
        {
            KeepFileNameOnly(path);
            TempData["Error"] = "The resume was saved but Claude could not read it: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            KeepFileNameOnly(path);
            TempData["Error"] = "Reading the resume took too long. It was saved - fill the details in by hand, or try again.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult Save([FromForm] UserProfile profile, string? skillsText)
    {
        var current = _profile.Current;

        // Never trust the posted resume file name: it is turned into a path.
        profile.ResumeFileName = current.ResumeFileName;
        profile.Skills = (skillsText ?? "")
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        profile.Answers = (profile.Answers ?? new List<SavedAnswer>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Question) && !string.IsNullOrWhiteSpace(a.Answer))
            .ToList();

        _profile.Save(profile);
        TempData["Message"] = "Profile saved.";
        return RedirectToAction(nameof(Index));
    }

    private void KeepFileNameOnly(string path)
    {
        var profile = ProfileService.Clone(_profile.Current);
        profile.ResumeFileName = Path.GetFileName(path);
        _profile.Save(profile);
    }
}
