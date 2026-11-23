using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Persistence;

/// <summary>
/// Earlier versions kept every job in <c>jobs/&lt;id&gt;/job.json</c>. The first start with a database moves them in, so no history is lost.
/// Each imported file is renamed to <c>job.json.imported</c>: it stays as a backup and is never read again.
/// A job those versions left unfinished is imported as failed ("interrupted"), as they showed it after a restart, and is not run again by
/// itself: the file can be days old, with no way to know whether its application already went through. The user retries it if wanted.
/// </summary>
public static class LegacyJobImporter
{
    public const string FileName = "job.json";
    public const string ImportedFileName = "job.json.imported";

    /// <summary>Imports the jobs that are not in the database yet. Returns how many were imported.</summary>
    public static int Import(AppPaths paths, IJobPersistence persistence, ILogger logger)
    {
        if (!Directory.Exists(paths.JobsRoot)) return 0;

        var known = persistence.Ids();
        var imported = 0;
        foreach (var folder in Directory.GetDirectories(paths.JobsRoot))
        {
            var file = Path.Combine(folder, FileName);
            if (!File.Exists(file)) continue;

            var record = JsonFile.Read<JobRecord>(file);
            if (record == null)
            {
                logger.LogWarning("Skipped {File}: it could not be read as a job.", file);
                continue;
            }

            try
            {
                if (!known.Contains(record.Id))
                {
                    if (!record.Status.IsTerminal()) MarkInterrupted(record);
                    persistence.Save(record);
                }
                File.Move(file, Path.Combine(folder, ImportedFileName), overwrite: true);
                imported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Imported {File}, but could not rename it afterwards.", file);
            }
        }

        if (imported > 0) logger.LogInformation("Imported {Count} job(s) from the earlier job.json files into the database.", imported);
        return imported;
    }

    private static void MarkInterrupted(JobRecord record)
    {
        record.Status = JobStatus.Failed;
        record.StatusDetail = "Interrupted: the app was restarted while this job was running. Press Retry to continue.";
        record.PendingHelp = null;
        record.LimitHit = false;
        record.Log.Add(new LogEntry { Kind = "warn", Text = "Imported from an earlier version while unfinished. Press Retry to continue it; if an application was being submitted, check the employer's site first." });
    }
}
