using JobApplier.Infrastructure;

namespace JobApplier.Tests;

/// <summary>A throw-away data folder for tests that touch the disk.</summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "jobapplier-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new AppPaths(Root);
    }

    public string Root { get; }
    public AppPaths Paths { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* temp folder, harmless */ }
    }
}
