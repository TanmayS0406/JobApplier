using System.ComponentModel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobApplier.Agents;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using Microsoft.Agents.AI;

namespace JobApplier.Services;

/// <summary>The part of the profile that comes from the resume (what the import agent returns).</summary>
public sealed class ResumeExtract
{
    public string FullName { get; set; } = "";

    [Description("Primary e-mail address on the resume. Empty string if none.")]
    public string Email { get; set; } = "";

    public string Phone { get; set; } = "";
    public string Location { get; set; } = "";
    public string LinkedInUrl { get; set; } = "";
    public string GitHubUrl { get; set; } = "";
    public string PortfolioUrl { get; set; } = "";
    public string CurrentTitle { get; set; } = "";
    public string CurrentCompany { get; set; } = "";

    [Description("Total years of professional experience as a number, or empty string if unclear.")]
    public string YearsOfExperience { get; set; } = "";

    [Description("Two to four sentence professional summary written from the resume.")]
    public string Summary { get; set; } = "";

    public List<string> Skills { get; set; } = new();
    public string Education { get; set; } = "";

    [Description("The complete plain text of the resume, all sections, in reading order.")]
    public string ResumeText { get; set; } = "";
}

/// <summary>Reads an uploaded resume with Claude and turns it into profile fields (name, e-mail, skills, full text).</summary>
public sealed class ProfileImporter
{
    private readonly AgentFactory _agents;
    private readonly ProfileService _profile;
    private readonly AppPaths _paths;

    public ProfileImporter(AgentFactory agents, ProfileService profile, AppPaths paths)
    {
        _agents = agents;
        _profile = profile;
        _paths = paths;
    }

    public static readonly string[] SupportedExtensions = { ".pdf", ".docx", ".txt", ".md", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Extracts the profile from the stored resume and saves it. Fields the user typed by hand (work authorisation, notice
    /// period, salary, notes, saved answers) are kept.
    /// </summary>
    public async Task<UserProfile> ImportAsync(string resumePath, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(_paths.Root, "import", "workspace");
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        Directory.CreateDirectory(workspace);

        // Claude's Read tool opens PDFs, images and text, but not Word files - so .docx is converted to text first.
        string readable;
        if (resumePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            readable = "resume.txt";
            File.WriteAllText(Path.Combine(workspace, readable), DocxText.Extract(resumePath));
        }
        else
        {
            readable = "resume" + Path.GetExtension(resumePath).ToLowerInvariant();
            File.Copy(resumePath, Path.Combine(workspace, readable), overwrite: true);
        }

        var agent = _agents.CreateStandalone("resume-reader", Prompts.ResumeInstructions, workspace, Path.Combine(_paths.Root, "import", "private"));
        var response = await agent.RunAsync<ResumeExtract>(
            Prompts.ResumePrompt(readable), session: null, serializerOptions: JsonFile.Options, options: null, cancellationToken: cancellationToken);
        var extract = response.Result;

        var profile = ProfileService.Clone(_profile.Current);
        profile.FullName = Pick(extract.FullName, profile.FullName);
        profile.Email = Pick(extract.Email, profile.Email);
        profile.Phone = Pick(extract.Phone, profile.Phone);
        profile.Location = Pick(extract.Location, profile.Location);
        profile.LinkedInUrl = Pick(extract.LinkedInUrl, profile.LinkedInUrl);
        profile.GitHubUrl = Pick(extract.GitHubUrl, profile.GitHubUrl);
        profile.PortfolioUrl = Pick(extract.PortfolioUrl, profile.PortfolioUrl);
        profile.CurrentTitle = Pick(extract.CurrentTitle, profile.CurrentTitle);
        profile.CurrentCompany = Pick(extract.CurrentCompany, profile.CurrentCompany);
        profile.YearsOfExperience = Pick(extract.YearsOfExperience, profile.YearsOfExperience);
        profile.Summary = Pick(extract.Summary, profile.Summary);
        profile.Education = Pick(extract.Education, profile.Education);
        if (extract.Skills.Count > 0) profile.Skills = extract.Skills.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
        profile.ResumeText = Pick(extract.ResumeText, profile.ResumeText);
        profile.ResumeFileName = Path.GetFileName(resumePath);

        _profile.Save(profile);
        return profile;
    }

    private static string Pick(string? fresh, string existing) => string.IsNullOrWhiteSpace(fresh) ? existing : fresh.Trim();
}

public static class DocxText
{
    /// <summary>Plain text of a .docx: one line per paragraph, including paragraphs inside tables.</summary>
    public static string Extract(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;

        var lines = body.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim())
            .Where(l => l.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }
}
