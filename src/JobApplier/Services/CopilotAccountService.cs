using JobApplier.Claude;

namespace JobApplier.Services;

/// <summary>
/// Sign-in state of the GitHub Copilot CLI, in the same shape as the Claude one so the UI can treat both alike.
/// <paramref name="Source"/> says where the sign-in comes from: <c>token-env</c> (a token variable), <c>stored</c> (a sign-in made with
/// <c>copilot login</c>), or empty. <paramref name="CanInstall"/> is whether this PC has something to install the CLI with (winget or npm).
/// </summary>
public sealed record CopilotAuthStatus(bool CliFound, bool LoggedIn, string Detail, string Source = "", bool CanInstall = false);

/// <summary>
/// Sign-in for the GitHub Copilot CLI, the same way as for Claude: the CLI's own OAuth flow (<c>copilot login</c>) in a console window and
/// the browser, with the token kept by the CLI. JobApplier never sees a GitHub credential. The CLI has no "who am I" command, so the state
/// is read from what it leaves behind (see <see cref="CopilotCli.HasStoredLogin"/>); that tells whether a sign-in exists, not whether it is
/// still valid, and an expired one shows up as a clear error on the first job.
/// </summary>
public sealed class CopilotAccountService
{
    private readonly SettingsService _settings;
    private readonly string? _configFolder;
    private readonly Func<string, string?>? _environment;
    private readonly Func<IReadOnlyList<string>>? _credentialTargets;

    /// <param name="configFolder">The CLI's settings folder; null = the real one (the parameters are for tests).</param>
    /// <param name="environment">Environment lookup; null = the real one.</param>
    /// <param name="credentialTargets">The credential store's entries; null = ask the system.</param>
    public CopilotAccountService(SettingsService settings, string? configFolder = null, Func<string, string?>? environment = null,
                                 Func<IReadOnlyList<string>>? credentialTargets = null)
    {
        _settings = settings;
        _configFolder = configFolder;
        _environment = environment;
        _credentialTargets = credentialTargets;
    }

    public string? CliPath => CopilotCli.Locate(_settings.Current.CopilotPath);

    public CopilotAuthStatus GetStatus()
    {
        var found = CliPath != null;
        var canInstall = CopilotCli.Installer() != null;

        if (CopilotCli.TokenVariable(_environment) is { } variable)
            return new CopilotAuthStatus(found, true, "token (" + variable + ")", "token-env", canInstall);
        if (CopilotCli.HasStoredLogin(_configFolder, _credentialTargets))
            return new CopilotAuthStatus(found, true, "GitHub account", "stored", canInstall);

        return new CopilotAuthStatus(found, false, "Not signed in.", "", canInstall);
    }

    /// <summary>Opens a console that installs the CLI. Returns an error message, or null when the window was opened.</summary>
    public string? StartInstall()
    {
        if (CliPath != null) return "The GitHub Copilot CLI is already installed.";
        try
        {
            CopilotCli.StartInstall();
            return null;
        }
        catch (FileNotFoundException)
        {
            return "Neither winget nor npm was found, so the CLI cannot be installed from here. Install Node.js 22 or newer from https://nodejs.org and run '" +
                   CopilotCli.NpmInstallCommand + "', or see https://docs.github.com/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli.";
        }
        catch (Exception ex)
        {
            return "Could not open the install window: " + ex.Message + " You can run it yourself: " + CopilotCli.WingetInstallCommand + " (or " + CopilotCli.NpmInstallCommand + ")";
        }
    }

    /// <summary>Opens the OAuth sign-in (<c>copilot login</c>). Returns an error message, or null when the sign-in window was opened.</summary>
    public string? StartSignIn()
    {
        var cli = CliPath;
        if (cli == null) return "GitHub Copilot CLI not found. Install it with the button above, or set its path below.";
        try
        {
            CopilotCli.StartLogin(cli);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not open the sign-in window: " + ex.Message;
        }
    }

    /// <summary>
    /// The CLI can only be signed out from inside itself (<c>/logout</c>), so this opens it with the instruction. A token variable cannot be
    /// signed out of from here at all, and says so.
    /// </summary>
    public string? StartSignOut()
    {
        var cli = CliPath;
        if (cli == null) return "GitHub Copilot CLI not found.";
        if (CopilotCli.TokenVariable(_environment) is { } variable)
            return $"The sign-in comes from the {variable} environment variable. Remove that variable to sign out.";
        try
        {
            CopilotCli.StartSignOut(cli);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not open the window: " + ex.Message;
        }
    }
}
