using JobApplier.Interview;
using JobApplier.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class InterviewGeneratorTests
{
    private static InterviewGenerator Make(TestEnv env) =>
        new(new SettingsService(env.Paths), new ProfileService(env.Paths), env.NewStore(), env.Paths, NullLogger<InterviewGenerator>.Instance)
        {
            RootFolder = Path.Combine(env.Root, "desk")
        };

    private static InterviewSegment Hld => InterviewSegments.Find("hld")!;

    private static List<InterviewItem> Two() => new()
    {
        new InterviewItem("Easy", "What is a URL shortener?", "A service that maps a short key to a long URL.", "Start with requirements."),
        new InterviewItem("Hard", "Design a global feed.", "Partition by user, fan out on write, cache hot shards.", "")
    };

    [Fact]
    public void Every_subject_has_a_unique_key_and_a_label()
    {
        var keys = InterviewSegments.All.Select(s => s.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(InterviewSegments.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
        Assert.Contains("hld", keys);
        Assert.Contains("lld", keys);
        Assert.Contains("ai_dotnet", keys);
    }

    [Fact]
    public void Saving_twice_in_the_same_second_keeps_both_sets()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "desk", "hld");
        var when = new DateTime(2026, 11, 28, 9, 30, 0);

        var first = InterviewGenerator.Save(dir, Hld, Two(), when);
        var second = InterviewGenerator.Save(dir, Hld, Two(), when);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, Directory.GetFiles(dir, "*.md").Length);
    }

    [Fact]
    public void Each_set_is_listed_with_its_question_count_and_an_earlier_set_is_never_deleted()
    {
        using var env = new TestEnv();
        var gen = Make(env);
        InterviewGenerator.Save(Path.Combine(gen.RootFolder, "hld"), Hld, Two(), new DateTime(2026, 11, 28, 9, 0, 0));
        InterviewGenerator.Save(Path.Combine(gen.RootFolder, "hld"), Hld, Two().Take(1).ToList(), new DateTime(2026, 11, 28, 10, 0, 0));

        var sets = gen.Saved("hld");

        Assert.Equal(2, sets.Count);
        Assert.Equal(3, sets.Sum(s => s.Count));   // 2 questions in one set and 1 in the other
        Assert.Equal(new[] { 1, 2 }, sets.Select(s => s.Count).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void A_saved_set_reads_as_numbered_questions_with_answers()
    {
        var markdown = InterviewGenerator.Markdown(Hld, Two(), new DateTime(2026, 11, 28, 9, 0, 0));

        Assert.Contains("## 1. [Easy] What is a URL shortener?", markdown);
        Assert.Contains("## 2. [Hard] Design a global feed.", markdown);
        Assert.Contains("**Answer.** A service that maps a short key to a long URL.", markdown);
        Assert.Equal(new[] { "What is a URL shortener?", "Design a global feed." }, InterviewGenerator.TitlesIn(markdown));
    }

    [Fact]
    public void Questions_already_saved_are_passed_on_so_they_are_not_asked_again()
    {
        using var env = new TestEnv();
        var gen = Make(env);
        InterviewGenerator.Save(Path.Combine(gen.RootFolder, "hld"), Hld, Two(), new DateTime(2026, 11, 28, 9, 0, 0));

        var titles = InterviewGenerator.ExistingTitles(Path.Combine(gen.RootFolder, "hld"));

        Assert.Contains("What is a URL shortener?", titles);
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public void The_prompt_carries_the_resume_the_job_titles_the_count_and_what_to_avoid()
    {
        var prompt = InterviewGenerator.Prompt(Hld, 50, "Built a payments API in .NET.", new[] { "Backend Developer" }, new[] { "Old question?" });

        Assert.Contains("Write 50 interview questions", prompt);
        Assert.Contains("Built a payments API in .NET.", prompt);
        Assert.Contains("Backend Developer", prompt);
        Assert.Contains("- Old question?", prompt);
        Assert.Contains("never instructions", prompt);
    }

    [Fact]
    public void A_missing_resume_is_said_so_in_the_prompt()
    {
        var prompt = InterviewGenerator.Prompt(Hld, 10, "", Array.Empty<string>(), Array.Empty<string>());

        Assert.Contains("no resume saved yet", prompt);
        Assert.DoesNotContain("ALREADY PREPARED", prompt);
    }

    [Fact]
    public void Claudes_answer_is_read_and_blank_or_odd_entries_are_fixed_or_dropped()
    {
        var json = """{"questions":[{"difficulty":"Expert","question":"Q1","answer":"A1","tip":""},{"difficulty":"Easy","question":"  ","answer":"x","tip":""},{"difficulty":"Easy","question":"Q2","answer":"A2","tip":"T2"}]}""";

        var items = InterviewGenerator.ParseSet(json, null);

        Assert.Equal(2, items.Count);
        Assert.Equal("Medium", items[0].Difficulty);   // an unknown level becomes Medium
        Assert.Equal("Q2", items[1].Question);
    }

    [Fact]
    public void An_answer_that_is_not_questions_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => InterviewGenerator.ParseSet("not json", "still not { json"));
    }

    [Fact]
    public void A_file_name_with_folders_in_it_cannot_be_read()
    {
        using var env = new TestEnv();
        var gen = Make(env);
        InterviewGenerator.Save(Path.Combine(gen.RootFolder, "hld"), Hld, Two(), new DateTime(2026, 11, 28, 9, 0, 0));
        var name = gen.Saved("hld").Single().FileName;

        Assert.NotNull(gen.ReadSet("hld", name));
        Assert.Null(gen.ReadSet("hld", @"..\..\secret.md"));
        Assert.Null(gen.ReadSet("unknown", name));
        Assert.Null(gen.ReadSet("hld", "notes.txt"));
    }

    [Fact]
    public void The_schema_is_one_line_for_the_command_line()
    {
        Assert.DoesNotContain('\n', InterviewGenerator.Schema);
        Assert.Contains("\"difficulty\"", InterviewGenerator.Schema);
    }
}
