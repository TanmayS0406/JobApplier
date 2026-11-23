using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JobApplier.Persistence;

public static class DatabaseStartup
{
    public const string ConnectionName = "JobApplier";

    /// <summary>Used when nothing is configured: the SQL Server Express instance on this machine, signed in as the Windows user.</summary>
    public const string DefaultConnectionString =
        @"Server=localhost\SQLEXPRESS;Database=JobApplier;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

    public static string ConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionName) is { Length: > 0 } configured ? configured : DefaultConnectionString;

    /// <summary>
    /// Creates the database if it is missing and brings it to the current schema by applying the pending migrations. Runs before anything
    /// reads a job (and before Hangfire opens its own tables in the same database). A database that cannot be reached stops the app
    /// with a message that says what to change, instead of a stack trace from the middle of a request.
    /// </summary>
    public static void Migrate(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("JobApplier.Database");
        var factory = services.GetRequiredService<IDbContextFactory<JobApplierDbContext>>();
        try
        {
            using var db = factory.CreateDbContext();
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Count > 0) logger.LogInformation("Updating the database: {Migrations}", string.Join(", ", pending));

            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "JobApplier could not open or update its SQL Server database. Check that SQL Server is running and that the connection string " +
                "'ConnectionStrings:{Name}' in appsettings.json (or the ConnectionStrings__{Name} environment variable) is right.", ConnectionName, ConnectionName);
            throw;
        }
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without starting the app (which would itself run the migrations).</summary>
public sealed class JobApplierDbContextFactory : IDesignTimeDbContextFactory<JobApplierDbContext>
{
    public JobApplierDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__" + DatabaseStartup.ConnectionName) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : DatabaseStartup.DefaultConnectionString;
        return new JobApplierDbContext(JobApplierDbOptions.Create(connection));
    }
}

public static class JobApplierDbOptions
{
    public static DbContextOptions<JobApplierDbContext> Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<JobApplierDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
}
