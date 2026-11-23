using JobApplier.Domain;

namespace JobApplier.Persistence;

/// <summary>Where <see cref="Services.JobStore"/> keeps jobs between runs of the app.</summary>
public interface IJobPersistence
{
    /// <summary>Every saved job.</summary>
    IReadOnlyList<JobRecord> LoadAll();

    /// <summary>The ids of the saved jobs, without reading the jobs themselves.</summary>
    IReadOnlySet<Guid> Ids();

    /// <summary>Saves the job: inserts it, or updates it when it is already saved. The caller holds the job's lock for the duration.</summary>
    void Save(JobRecord record);

    /// <summary>Removes the job. Not an error when it is not there.</summary>
    void Delete(Guid id);
}
