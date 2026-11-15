using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// Remembers every account the agent created (company, e-mail, password) in <c>accounts.json</c> and every posting it applied
/// to (company, role, URL) in <c>applications.json</c>, and keeps the Word document on the Desktop in sync with both.
/// The applications list is what stops the same posting being applied to twice, even after its job has been deleted.
/// </summary>
public sealed class AccountService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly ILogger<AccountService> _logger;
    private readonly object _gate = new();
    private readonly List<AccountRecord> _records;
    private readonly List<ApplicationRecord> _applications;

    public AccountService(AppPaths paths, SettingsService settings, ILogger<AccountService> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _records = JsonFile.Read<List<AccountRecord>>(paths.AccountsFile) ?? new List<AccountRecord>();
        _applications = JsonFile.Read<List<ApplicationRecord>>(paths.ApplicationsFile) ?? new List<ApplicationRecord>();
    }

    /// <summary>Set when the document could not be written (for example Word has it open); null when it is up to date.</summary>
    public string? DocumentError { get; private set; }

    public string DocumentPath => _settings.AccountsDocumentPath;

    public IReadOnlyList<AccountRecord> All()
    {
        lock (_gate) return _records.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    /// <summary>
    /// The account already saved for this site and e-mail, if any. A later job on the same site must reuse its password:
    /// the site would reject a freshly generated one for an e-mail that is already registered.
    /// </summary>
    public AccountRecord? FindForSite(string siteOrUrl, string email)
    {
        var key = Key(siteOrUrl, email);
        lock (_gate) return _records.FirstOrDefault(r => Key(r.Site, r.Email) == key);
    }

    /// <summary>
    /// Records an account. One row per site and e-mail: a second job on the same site updates that row (and adds its role)
    /// instead of listing the same account twice.
    /// </summary>
    public AccountRecord Add(AccountRecord record)
    {
        lock (_gate)
        {
            var key = Key(record.Site, record.Email);
            var existing = _records.FirstOrDefault(r => Key(r.Site, r.Email) == key);

            if (existing != null)
            {
                existing.Password = record.Password; // the most recently confirmed credentials win
                if (record.Role.Length > 0 && !existing.Role.Contains(record.Role, StringComparison.OrdinalIgnoreCase))
                    existing.Role = existing.Role.Length == 0 ? record.Role : existing.Role + "; " + record.Role;
                if (record.Note.Length > 0) existing.Note = record.Note;
                record = existing;
            }
            else
            {
                _records.Add(record);
            }

            JsonFile.Write(_paths.AccountsFile, _records);
            RewriteDocumentLocked();
            return record;
        }
    }

    public IReadOnlyList<ApplicationRecord> Applications()
    {
        lock (_gate) return _applications.OrderByDescending(a => a.AppliedUtc).ToList();
    }

    /// <summary>
    /// The earlier application to the same posting, if any. Addresses are compared with <see cref="PostingKey"/>, so tracking
    /// parameters do not matter. A job's own entry is not a duplicate of itself.
    /// </summary>
    public ApplicationRecord? FindApplication(Guid jobId, string? postingUrl)
    {
        var key = PostingKey.From(postingUrl);
        if (key == null) return null;
        lock (_gate) return _applications.FirstOrDefault(a => a.JobId != jobId && PostingKey.From(a.Url) == key);
    }

    /// <summary>
    /// Records an application. One row per posting: recording the same posting again keeps the first row (and its date).
    /// The list is saved before the document is rewritten, so a locked document never loses the entry.
    /// </summary>
    public ApplicationRecord RecordApplication(ApplicationRecord record)
    {
        lock (_gate)
        {
            var key = PostingKey.From(record.Url);
            var existing = key == null ? null : _applications.FirstOrDefault(a => PostingKey.From(a.Url) == key);
            if (existing != null) return existing;

            _applications.Add(record);
            JsonFile.Write(_paths.ApplicationsFile, _applications);
            RewriteDocumentLocked();
            return record;
        }
    }

    /// <summary>Rebuilds the document from <c>accounts.json</c> and <c>applications.json</c>. Returns an error message, or null on success.</summary>
    public string? RewriteDocument()
    {
        lock (_gate)
        {
            RewriteDocumentLocked();
            return DocumentError;
        }
    }

    /// <summary>The host of a site address ("https://www.jobs.acme.com/x" and "jobs.acme.com" both give "jobs.acme.com").</summary>
    public static string HostOf(string siteOrUrl)
    {
        var text = (siteOrUrl ?? "").Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            text = uri.Host;
        else if (Uri.TryCreate("https://" + text, UriKind.Absolute, out uri) && uri.Host.Contains('.'))
            text = uri.Host;

        text = text.ToLowerInvariant();
        return text.StartsWith("www.", StringComparison.Ordinal) ? text[4..] : text;
    }

    private static string Key(string site, string email) => HostOf(site) + "|" + (email ?? "").Trim().ToLowerInvariant();

    private void RewriteDocumentLocked()
    {
        try
        {
            AccountsDocument.Write(DocumentPath, _records, _applications);
            DocumentError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DocumentError = $"Could not write {DocumentPath}: {ex.Message} (Close it in Word, then press 'Rebuild document' in Settings.)";
            _logger.LogWarning(ex, "Accounts document is locked or not writable");
        }
    }
}
