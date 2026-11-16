namespace JobApplier.Infrastructure;

/// <summary>Everything the app writes lives under one data folder (default %LOCALAPPDATA%\JobApplier).</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(JobsRoot);
        Directory.CreateDirectory(ResumeFolder);
    }

    public static AppPaths FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration["JobApplier:DataFolder"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobApplier")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        return new AppPaths(root);
    }

    public string Root { get; }
    public string JobsRoot => Path.Combine(Root, "jobs");
    public string ResumeFolder => Path.Combine(Root, "resume");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProfileFile => Path.Combine(Root, "profile.json");
    public string AccountsFile => Path.Combine(Root, "accounts.json");
    public string ApplicationsFile => Path.Combine(Root, "applications.json");

    /// <summary>The Gmail client id/secret and refresh token, encrypted for this Windows user (DPAPI).</summary>
    public string GmailVaultFile => Path.Combine(Root, "gmail.dat");

    /// <summary>Employer replies found in Gmail (sender, subject, short snippet, classification).</summary>
    public string RepliesFile => Path.Combine(Root, "replies.json");

    /// <summary>When Gmail was last checked and which messages have already been looked at.</summary>
    public string MailStateFile => Path.Combine(Root, "mail-state.json");

    public string DefaultAccountsDocument =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "JobApplier-Accounts.docx");

    public string JobFolder(Guid id) => Path.Combine(JobsRoot, id.ToString("N"));
    public string JobFile(Guid id) => Path.Combine(JobFolder(id), "job.json");

    /// <summary>Working directory of the Claude process. The agent's file access is confined to it.</summary>
    public string Workspace(Guid id) => Path.Combine(JobFolder(id), "workspace");

    /// <summary>Holds secrets and MCP config (bearer token). Deliberately outside the workspace so the agent cannot read it.</summary>
    public string PrivateFolder(Guid id) => Path.Combine(JobFolder(id), "private");

    public string EnsureWorkspace(Guid id)
    {
        var path = Workspace(id);
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsurePrivateFolder(Guid id)
    {
        var path = PrivateFolder(id);
        Directory.CreateDirectory(path);
        return path;
    }
}
