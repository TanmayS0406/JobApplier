using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Tests;

public class BrowserScriptTests
{
    private static string SiteScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "JobApplier", "wwwroot", "js", "site.js");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("site.js not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The help buttons are named "action" (that is the controller's parameter). A control named like a form property replaces
    /// that property, so <c>form.action</c> is a list of buttons, not the URL: every answer was posted to
    /// "/[object RadioNodeList]" and got a 404. Read the attribute instead.
    /// </summary>
    [Fact]
    public void The_script_never_reads_the_action_property_of_a_form()
    {
        var code = System.Text.RegularExpressions.Regex.Replace(SiteScript(), @"//.*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.DoesNotMatch(@"\b[a-zA-Z_]*[fF]orm\.action\b", code);
        Assert.Contains("getAttribute('action')", code);
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generates_requested_length_with_every_character_class()
    {
        for (var i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate(16);
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => "!@%&*?".Contains(c));
        }
    }

    [Fact]
    public void Never_uses_characters_that_break_dotenv_files_or_look_alike()
    {
        var all = string.Concat(Enumerable.Range(0, 500).Select(_ => PasswordGenerator.Generate()));
        foreach (var bad in "#$\"'\\` 0O1lI") Assert.DoesNotContain(bad, all);
    }

    [Fact]
    public void Passwords_are_unique()
    {
        var set = Enumerable.Range(0, 1000).Select(_ => PasswordGenerator.Generate()).ToHashSet();
        Assert.Equal(1000, set.Count);
    }

    [Fact]
    public void Rejects_short_lengths() => Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(8));
}

public class SecretRedactorTests
{
    [Fact]
    public void Masks_every_occurrence()
    {
        var result = SecretRedactor.Redact("typed Abc!1234xyz then Abc!1234xyz again", "Abc!1234xyz");
        Assert.DoesNotContain("Abc!1234xyz", result);
        Assert.Equal($"typed {SecretRedactor.Mask} then {SecretRedactor.Mask} again", result);
    }

    [Fact]
    public void Ignores_null_empty_and_very_short_secrets()
    {
        Assert.Equal("a b c", SecretRedactor.Redact("a b c", null, "", "b"));
    }

    [Fact]
    public void Handles_null_text() => Assert.Equal("", SecretRedactor.Redact(null, "secret-value"));
}

public class ImageSnifferTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, ".gif")]
    public void Recognises_images_by_content(byte[] header, string expected) =>
        Assert.Equal(expected, ImageSniffer.Detect(header));

    [Fact]
    public void Recognises_webp()
    {
        var header = "RIFF\0\0\0\0WEBP"u8.ToArray();
        Assert.Equal(".webp", ImageSniffer.Detect(header));
    }

    [Fact]
    public void Rejects_other_content_even_if_it_claims_to_be_an_image()
    {
        Assert.Null(ImageSniffer.Detect("MZ\x90\0 this is an exe"u8));
        Assert.Null(ImageSniffer.Detect("<svg onload=alert(1)>"u8));
        Assert.Null(ImageSniffer.Detect(ReadOnlySpan<byte>.Empty));
    }
}

public class JsonFileTests
{
    [Fact]
    public void Round_trips_enums_as_strings_and_never_leaves_temp_files()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "x.json");

        JsonFile.Write(path, new JobRecord { Status = JobStatus.Applying });

        Assert.Contains("\"Applying\"", File.ReadAllText(path));
        Assert.Equal(JobStatus.Applying, JsonFile.Read<JobRecord>(path)!.Status);
        Assert.Empty(Directory.GetFiles(env.Root, "*.tmp"));
    }

    [Fact]
    public void Reading_a_corrupt_file_returns_null_instead_of_throwing()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(JsonFile.Read<JobRecord>(path));
    }

    [Fact]
    public void Job_password_is_never_serialised()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "job.json");
        JsonFile.Write(path, new JobRecord { Password = "Sup3r!Secret-pass" });
        Assert.DoesNotContain("Sup3r!Secret-pass", File.ReadAllText(path));
    }
}

public class MatchResultTests
{
    [Fact]
    public void Weights_are_40_25_15_10_10()
    {
        // 100*.40 + 80*.25 + 60*.15 + 40*.10 + 20*.10 = 40 + 20 + 9 + 4 + 2 = 75
        Assert.Equal(75, MatchResult.ComputePercent(100, 80, 60, 40, 20));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(100, 100, 100, 100, 100, 100)]
    [InlineData(500, 500, 500, 500, 500, 100)]   // out-of-range scores from the model are clamped
    [InlineData(-20, -20, -20, -20, -20, 0)]
    public void Clamps_to_0_100(int a, int b, int c, int d, int e, int expected) =>
        Assert.Equal(expected, MatchResult.ComputePercent(a, b, c, d, e));
}
