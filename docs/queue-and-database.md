# The database and the queue

[← Back to the README](../README.md)

Jobs are stored in SQL Server (through EF Core) and run from a Hangfire queue kept in the same database, so closing the app loses
nothing. **Every start applies the database migrations by itself**: the `JobApplier` database is created if it is missing and brought
up to the current schema, then Hangfire creates its own tables (schema `HangFire`) next to it. Nothing to run by hand.

The connection string is `ConnectionStrings:JobApplier` in `appsettings.json`; it defaults to the Express instance on this PC with
your Windows login:

```
Server=localhost\SQLEXPRESS;Database=JobApplier;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False
```

Point it elsewhere with the environment variable `ConnectionStrings__JobApplier`, for example LocalDB:
`Server=(localdb)\MSSQLLocalDB;Database=JobApplier;Trusted_Connection=True`. If the database cannot be reached the app stops at start-up
and says which setting to check.

* **Closed or killed in the middle of work?** Start it again: every job that was not finished goes back in the queue, oldest first, and
  continues **from the stage it had reached** (reading the screenshot, finding the posting and scoring are stored on the job and are not
  redone). A job that was filling in a form starts that stage again; if the application had already gone through, the site says so and it
  is recorded as applied.
* **Out of tokens?** The job that hit the limit waits for the reset and **continues by itself**, with nothing to press: a recurring Hangfire
  check looks every minute, and the wait survives restarts. Jobs that have not started are held back until the limit is over, so they do
  not all fail the same way.
* **The queue dashboard** (*Queue* in the menu, or <http://127.0.0.1:5189/hangfire>) shows what is waiting, running or scheduled for after a
  limit. It is reachable from this PC only.
* **Changing the data model?** Add a migration, and the next start applies it:
  `dotnet ef migrations add <Name> --project src/JobApplier --output-dir Persistence/Migrations`.
* **Coming from an earlier version?** The first start imports your old `jobs/<id>/job.json` files into the database and renames each to
  `job.json.imported` (kept as a backup, never read again). A job the old version had left unfinished comes in as *Failed
  (interrupted)* and is **not** restarted by itself - the file can be days old and there is no way to know whether its application went
  through - so press Retry on it if you want it continued. The profile, settings, accounts and applications stay as the JSON files
  they were.
