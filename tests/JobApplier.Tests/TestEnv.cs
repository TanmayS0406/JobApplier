using JobApplier.Persistence;
using JobApplier.Infrastructure;
using JobApplier.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

/// <summary>
/// A throw-away data folder for tests that touch the disk, with a database of its own: the real EF mapping on a SQLite file in that folder
/// (the app uses SQL Server, which the tests must not need).
/// </summary>
public sealed class TestEnv : IDisposable
{
    private readonly DbContextOptions<JobApplierDbContext> _options;

    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "jobapplier-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new AppPaths(Root);

        _options = new DbContextOptionsBuilder<JobApplierDbContext>().UseSqlite("Data Source=" + Path.Combine(Root, "jobs.db")).Options;
        Factory = new Contexts(_options);
        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public IDbContextFactory<JobApplierDbContext> Factory { get; }

    /// <summary>
    /// A job store over this folder's database. Each call is a fresh one with nothing cached, which is what the app has after a restart:
    /// whatever an earlier store flushed is loaded, and nothing else.
    /// </summary>
    public JobStore NewStore() => new(Paths, NewPersistence(), NullLogger<JobStore>.Instance);

    public IJobPersistence NewPersistence() => new EfJobPersistence(Factory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // otherwise the database file is still open and the folder cannot be removed
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* temp folder, harmless */ }
    }

    private sealed class Contexts : IDbContextFactory<JobApplierDbContext>
    {
        private readonly DbContextOptions<JobApplierDbContext> _options;
        public Contexts(DbContextOptions<JobApplierDbContext> options) => _options = options;
        public JobApplierDbContext CreateDbContext() => new(_options);
    }
}

/// <summary>Stands in for Hangfire: remembers what was queued, scheduled and deleted, and runs nothing by itself.</summary>
internal sealed class RecordingJobQueue : IJobQueue
{
    private int _next;

    public List<(string Id, Guid JobId)> Enqueued { get; } = new();
    public List<(string Id, Guid JobId, DateTime RunAtUtc)> Scheduled { get; } = new();
    public List<string> Deleted { get; } = new();
    public int RetryChecksRegistered { get; private set; }

    /// <summary>Make <see cref="Enqueue"/> throw (the database is away).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Called as the queue is told, before the entry exists: shows what the job looked like at that moment.</summary>
    public Action<Guid>? OnEnqueue { get; set; }

    public string Enqueue(Guid jobId)
    {
        if (FailWith != null) throw FailWith;
        OnEnqueue?.Invoke(jobId);
        var id = "q" + Interlocked.Increment(ref _next);
        Enqueued.Add((id, jobId));
        return id;
    }

    public string Schedule(Guid jobId, DateTime runAtUtc)
    {
        var id = "q" + Interlocked.Increment(ref _next);
        Scheduled.Add((id, jobId, runAtUtc));
        return id;
    }

    public void Delete(string queueId) => Deleted.Add(queueId);

    public void KeepCheckingForRetries() => RetryChecksRegistered++;
}
