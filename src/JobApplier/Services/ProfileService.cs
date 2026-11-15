using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// The applicant's profile: what the resume says plus the extra answers forms ask for.
/// The e-mail address here is the one used for every account the agent creates.
/// </summary>
public sealed class ProfileService
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private UserProfile _current;

    public ProfileService(AppPaths paths)
    {
        _paths = paths;
        _current = JsonFile.Read<UserProfile>(paths.ProfileFile) ?? new UserProfile();
    }

    /// <summary>Immutable by convention: copy with <see cref="Clone"/> before editing, then call <see cref="Save"/>.</summary>
    public UserProfile Current
    {
        get { lock (_gate) return _current; }
    }

    public bool HasResume => Current.ResumeFileName.Length > 0 && File.Exists(ResumePath);

    public string ResumePath => Path.Combine(_paths.ResumeFolder, Current.ResumeFileName);

    public static UserProfile Clone(UserProfile profile)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(profile, JsonFile.Options);
        return System.Text.Json.JsonSerializer.Deserialize<UserProfile>(json, JsonFile.Options)!;
    }

    public void Save(UserProfile profile)
    {
        lock (_gate)
        {
            JsonFile.Write(_paths.ProfileFile, profile);
            _current = profile;
        }
    }

    /// <summary>
    /// The user's file name made safe to use as a path: no folders (some browsers send the full path), no characters Windows
    /// rejects, a sane length, and a lower-case extension. Falls back to "resume" when nothing is left of the name.
    /// </summary>
    internal static string SafeFileName(string? originalFileName)
    {
        var name = Path.GetFileName((originalFileName ?? "").Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name).Trim().TrimEnd('.');
        if (stem.Length == 0) stem = "resume";
        if (stem.Length > 100) stem = stem[..100].TrimEnd();
        return stem + extension;
    }

    /// <summary>Remembers an answer the user gave to a form question, so later applications do not ask again.</summary>
    public void AddAnswer(string question, string answer)
    {
        question = question.Trim();
        answer = answer.Trim();
        if (question.Length == 0 || answer.Length == 0) return;

        var profile = Clone(Current);
        var existing = profile.Answers.FirstOrDefault(a => string.Equals(a.Question, question, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Answer = answer;
        else profile.Answers.Add(new SavedAnswer { Question = question, Answer = answer });
        Save(profile);
    }

    /// <summary>
    /// Stores the uploaded resume in the data folder under the name the user gave it and returns its path (the old copy is
    /// removed). The name matters: it is what an employer's form shows next to the upload.
    /// </summary>
    public async Task<string> StoreResumeAsync(Stream content, string originalFileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.ResumeFolder, SafeFileName(originalFileName));

        foreach (var old in Directory.GetFiles(_paths.ResumeFolder))
        {
            try { File.Delete(old); } catch (IOException) { /* replaced below or harmless */ }
        }

        await using (var target = File.Create(path))
        {
            await content.CopyToAsync(target, cancellationToken);
        }
        return path;
    }
}
