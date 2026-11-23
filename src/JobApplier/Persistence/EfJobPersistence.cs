using System.Collections.Concurrent;
using JobApplier.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobApplier.Persistence;

/// <summary>
/// Saves jobs through EF Core. Each call uses a context of its own, so the background flush and the web requests never share one.
/// </summary>
public sealed class EfJobPersistence : IJobPersistence
{
    private readonly IDbContextFactory<JobApplierDbContext> _factory;

    /// <summary>Jobs known to be in the database, so the common case (an update) needs no extra query.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _saved = new();

    public EfJobPersistence(IDbContextFactory<JobApplierDbContext> factory) => _factory = factory;

    public IReadOnlyList<JobRecord> LoadAll()
    {
        using var db = _factory.CreateDbContext();
        var jobs = db.Jobs.AsNoTracking().ToList();
        foreach (var job in jobs) _saved[job.Id] = 0;
        return jobs;
    }

    public IReadOnlySet<Guid> Ids()
    {
        using var db = _factory.CreateDbContext();
        return db.Jobs.AsNoTracking().Select(j => j.Id).ToHashSet();
    }

    public void Save(JobRecord record)
    {
        if (_saved.ContainsKey(record.Id))
        {
            try
            {
                SaveOnce(record, update: true);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The row is gone (deleted behind the app's back): put it back.
                _saved.TryRemove(record.Id, out _);
            }
        }

        SaveOnce(record, update: Exists(record.Id));
        _saved[record.Id] = 0;
    }

    public void Delete(Guid id)
    {
        using var db = _factory.CreateDbContext();
        db.Jobs.Where(j => j.Id == id).ExecuteDelete();
        _saved.TryRemove(id, out _);
    }

    private bool Exists(Guid id)
    {
        using var db = _factory.CreateDbContext();
        return db.Jobs.AsNoTracking().Any(j => j.Id == id);
    }

    private void SaveOnce(JobRecord record, bool update)
    {
        using var db = _factory.CreateDbContext();
        if (update) db.Jobs.Update(record);
        else db.Jobs.Add(record);
        db.SaveChanges();
    }
}
