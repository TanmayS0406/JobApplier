using JobApplier.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JobApplier.Persistence;

/// <summary>
/// The relational home of the jobs. A job is one row: the fields the app filters and sorts on are columns, and the parts that are only
/// ever read and written as a whole (what the screenshot said, the posting that was found, the match score, the log, ...) are JSON columns.
/// The schema is created and updated by the migrations in <c>Persistence/Migrations</c>, which the app applies every time it starts.
/// Hangfire keeps its own tables next to these, in the <c>HangFire</c> schema.
/// </summary>
public class JobApplierDbContext : DbContext
{
    public JobApplierDbContext(DbContextOptions<JobApplierDbContext> options) : base(options) { }

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Every time in this app is UTC; SQL Server hands back "unspecified", which would make ToLocalTime() and comparisons ambiguous.
        configuration.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Words rather than numbers, so a row (or the JSON in it) can be read without a lookup table.
        configuration.Properties<JobStatus>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<ApplyResultKind>().HaveConversion<string>().HaveMaxLength(20);
        configuration.Properties<HelpKind>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRecord>(job =>
        {
            job.ToTable("Jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Id).ValueGeneratedNever();

            job.Property(j => j.ScreenshotFile).HasMaxLength(260);
            job.Property(j => j.OriginalFileName).HasMaxLength(512);
            job.Property(j => j.QueueId).HasMaxLength(64);

            // The two choices of the upload form: plain columns.
            job.ComplexProperty(j => j.Options, options =>
            {
                options.Property(o => o.AutoSubmit).HasColumnName("AutoSubmit");
                options.Property(o => o.MinMatchPercent).HasColumnName("MinMatchPercent");
            });

            // What each stage produced: one JSON document each, null until the stage has run.
            job.ComplexProperty(j => j.Extracted, c => c.ToJson());
            job.ComplexProperty(j => j.Found, c => c.ToJson());
            job.ComplexProperty(j => j.Match, c => c.ToJson());
            job.ComplexProperty(j => j.Apply, c => c.ToJson());
            job.ComplexProperty(j => j.PendingHelp, c => c.ToJson());
            job.ComplexCollection(j => j.Log, c => c.ToJson());

            // Lives in memory only (see JobRecord.Password): never written to the database.
            job.Ignore(j => j.Password);

            job.HasIndex(j => j.Status);
            job.HasIndex(j => j.CreatedUtc);
        });
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }
}
