using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JobApplier.Domain;

namespace JobApplier.Mail;

/// <summary>Text helpers shared by the matcher and the classifier.</summary>
public static class MailText
{
    /// <summary>Lower case, accents removed ("Reunión" is "reunion"), everything that is not a letter or digit turned into a single space.</summary>
    public static string Words(string? text)
    {
        var sb = new StringBuilder();
        var lastSpace = true;
        foreach (var c in StripAccents(text ?? ""))
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Like <see cref="Words"/> but keeps the punctuation, so sentences can still be told apart.</summary>
    public static string Prepare(string? text)
    {
        var lower = StripAccents(text ?? "").ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
        return Regex.Replace(lower, @"[ \t\r\f\v]+", " ").Trim();
    }

    public static string StripAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>
/// A message matched to an application. <paramref name="Strength"/> is how sure: 3 is the least that is accepted, 6 and up is certain.
/// <paramref name="CompanyInHeader"/> says the company was found in the sender or the subject, not only in the text: mail that merely
/// mentions the company in its text (a job alert, a newsletter) is far less likely to be an answer.
/// </summary>
public sealed record ReplyMatch(ApplicationRecord Application, int Strength, bool CompanyInHeader);

/// <summary>
/// Decides which application an e-mail is about. A message is accepted only when the company shows up in a place that means
/// something (the sender, the subject, or - for mail that reads like a recruiting message - the text) and the message as a whole
/// looks like one about an application. A newsletter from the same company, or a mail that merely contains the company's name in
/// passing, is not enough.
/// </summary>
public static class ReplyMatcher
{
    /// <summary>Services that send mail on an employer's behalf: their domain says nothing about which employer it is.</summary>
    private static readonly string[] ApplicantTrackingDomains =
    {
        "greenhouse.io", "greenhouse-mail.io", "lever.co", "hire.lever.co", "ashbyhq.com", "workday.com", "myworkday.com",
        "myworkdayjobs.com", "smartrecruiters.com", "icims.com", "taleo.net", "oraclecloud.com", "successfactors.com", "sap.com",
        "teamtailor.com", "teamtailor-mail.com", "personio.de", "personio.com", "factorialhr.com", "factorial.es", "workable.com",
        "bamboohr.com", "jobvite.com", "recruitee.com", "breezy.hr", "pinpointhq.com", "comeet.co", "join.com", "hirehive.com",
        "linkedin.com", "indeed.com", "glassdoor.com", "infojobs.net", "tecnoempleo.com", "welcometothejungle.com"
    };

    private static readonly HashSet<string> LegalSuffixes = new()
    {
        "inc", "llc", "ltd", "limited", "gmbh", "sl", "sa", "slu", "ag", "bv", "co", "corp", "corporation", "plc", "srl", "spa", "oy", "ab", "as", "pty", "llp", "lp", "company", "the"
    };

    private static readonly HashSet<string> GenericTails = new()
    {
        "group", "technologies", "technology", "solutions", "systems", "consulting", "services", "international", "global", "talent",
        "partners", "software", "digital", "labs", "studio", "recruitment", "staffing", "holding", "holdings"
    };

    private static readonly HashSet<string> RoleStopWords = new()
    {
        "the", "and", "for", "with", "remote", "hybrid", "onsite", "site", "madrid", "barcelona", "spain", "espana", "de", "en", "la", "el", "y", "con", "para"
    };

    private static readonly string[] ApplicationWords =
    {
        "application", "applied", "applying", "candidate", "candidacy", "interview", "position", "role", "vacancy", "recruit", "recruiter",
        "hiring", "careers", "talent", "job", "human resources", "candidatura", "entrevista", "proceso de seleccion", "seleccion", "oferta",
        "puesto", "vacante", "reclutamiento", "recursos humanos", "postulacion"
    };

    /// <summary>The best application this message is about, or null.</summary>
    /// <param name="selfEmail">The user's own address: mail the user sent is never a reply.</param>
    /// <param name="useBody">Whether the text may count as evidence (it is only loaded for mail that looks like a recruiting message).</param>
    public static ReplyMatch? Match(MailMessage mail, IReadOnlyList<ApplicationRecord> applications, string? selfEmail, bool useBody)
    {
        if (!string.IsNullOrEmpty(selfEmail) && mail.FromAddress.Equals(selfEmail.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (mail.Labels.Contains("CATEGORY_PROMOTIONS") || mail.Labels.Contains("CATEGORY_SOCIAL") || mail.Labels.Contains("SENT") || mail.Labels.Contains("DRAFT")) return null;

        var subject = MailText.Words(mail.Subject);
        var sender = MailText.Words(mail.FromName);
        var text = MailText.Words(mail.Snippet + " " + (useBody ? mail.Body : ""));
        var hasApplicationWords = ContainsAny(subject + " " + text, ApplicationWords);

        ReplyMatch? best = null;
        foreach (var app in applications)
        {
            if (mail.DateUtc < app.AppliedUtc.AddDays(-1)) continue;   // it cannot be a reply to something not yet sent

            var company = CompanyForms(app.Company);
            if (company.Count == 0) continue;

            var score = 0;
            var fromCompany = SenderIsCompany(mail, app, company, sender);
            var inSubject = ContainsPhrase(subject, company, singleWordNeedsContext: false);
            if (fromCompany) score += 4;
            if (inSubject) score += 3;
            if (ContainsPhrase(text, company, singleWordNeedsContext: true)) score += 2;

            var companyEvidence = score;
            if (companyEvidence < 2) continue;          // never accept a message that does not name the company somewhere meaningful

            score += RoleScore(app.Role, subject + " " + text);
            if (hasApplicationWords) score += 1;
            if (IsApplicantTracking(mail.Domain)) score += 1;
            if (score < 3) continue;

            // Several applications to one company: the best role match wins, then the one applied to most recently before this mail.
            if (best == null || score > best.Strength || (score == best.Strength && app.AppliedUtc > best.Application.AppliedUtc))
                best = new ReplyMatch(app, score, fromCompany || inSubject);
        }
        return best;
    }

    /// <summary>
    /// True when a message that matched no application by sender and subject is still worth reading in full: it comes from a
    /// recruiting system, or its subject or snippet talks about an application.
    /// </summary>
    public static bool LooksLikeApplicationMail(MailMessage mail) =>
        IsApplicantTracking(mail.Domain) || ContainsAny(MailText.Words(mail.Subject + " " + mail.Snippet), ApplicationWords);

    // ---- company ----

    /// <summary>The names a company can appear under: "Vortech PCI Group Ltd" is also "Vortech PCI". Each as space-separated words.</summary>
    internal static IReadOnlyList<string> CompanyForms(string company)
    {
        var tokens = MailText.Words(company).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !LegalSuffixes.Contains(t)).ToList();
        var forms = new List<string>();
        if (tokens.Count > 0) forms.Add(string.Join(' ', tokens));

        var core = tokens.ToList();
        while (core.Count > 1 && GenericTails.Contains(core[^1])) core.RemoveAt(core.Count - 1);
        if (core.Count > 0 && core.Count < tokens.Count) forms.Add(string.Join(' ', core));

        return forms.Where(f => f.Length >= 3).Distinct().ToList();
    }

    /// <summary>A one-word company name is often an ordinary word ("Board", "Talent"): it only counts in the sender or the subject, not in the text.</summary>
    private static bool ContainsPhrase(string haystack, IReadOnlyList<string> forms, bool singleWordNeedsContext)
    {
        foreach (var form in forms)
        {
            if (singleWordNeedsContext && !form.Contains(' ')) continue;
            if ((" " + haystack + " ").Contains(" " + form + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool SenderIsCompany(MailMessage mail, ApplicationRecord app, IReadOnlyList<string> company, string senderName)
    {
        if (ContainsPhrase(senderName, company, singleWordNeedsContext: false)) return true;

        var domain = mail.Domain;
        if (domain.Length == 0 || IsApplicantTracking(domain)) return false;

        // jobs@wolterskluwer.com for "Wolters Kluwer": a label of the domain equals the name without spaces.
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var form in company)
        {
            var slug = form.Replace(" ", "");
            if (slug.Length < 4) continue;
            if (labels.Any(l => l == slug || (slug.Length >= 6 && l.Contains(slug)) || (l.Length >= 6 && slug.Contains(l)))) return true;
        }

        // Mail from the very domain the application was made on (careers.acme.com -> acme.com).
        return Uri.TryCreate(app.Url, UriKind.Absolute, out var posting)
               && !IsApplicantTracking(posting.Host)
               && Registrable(posting.Host) == Registrable(domain);
    }

    private static string Registrable(string host)
    {
        var labels = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2) return string.Join('.', labels);
        var secondLevel = labels[^2] is "co" or "com" or "org" or "net" or "gov" or "ac";   // acme.co.uk
        return string.Join('.', labels[^(secondLevel && labels[^1].Length == 2 ? 3 : 2)..]);
    }

    private static bool IsApplicantTracking(string domain) =>
        domain.Length > 0 && ApplicantTrackingDomains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));

    // ---- role ----

    private static int RoleScore(string role, string haystack)
    {
        var tokens = MailText.Words(role).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !RoleStopWords.Contains(t)).Distinct().ToList();
        if (tokens.Count == 0) return 0;

        var padded = " " + haystack + " ";
        var found = tokens.Count(t => padded.Contains(" " + t + " ", StringComparison.Ordinal));
        if (found >= 2 && found * 2 >= tokens.Count) return 2;
        return found >= 1 ? 1 : 0;
    }

    private static bool ContainsAny(string text, string[] words)
    {
        var padded = " " + text + " ";
        return words.Any(w => padded.Contains(" " + w, StringComparison.Ordinal));
    }
}
