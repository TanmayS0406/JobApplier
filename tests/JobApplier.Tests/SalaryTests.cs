using System.Text.Json;
using JobApplier.Agents;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Services;

namespace JobApplier.Tests;

public class SalaryTests
{
    private static readonly FoundPosting Posting = new() { Found = true, Url = "https://jobs.example.com/1", Title = "Dev", Company = "Acme", Portal = "Greenhouse", Description = "Build things in Madrid" };

    [Fact]
    public void The_apply_prompt_gives_the_salary_where_it_is_for_and_says_to_quote_an_equivalent_amount_elsewhere()
    {
        var profile = new UserProfile { FullName = "Jane", Email = "j@example.com", ExpectedSalary = "EUR 75000", SalaryBasis = "Berlin, Germany", SalaryAdjust = true };

        var prompt = Prompts.ApplyPrompt(Posting, profile, "resume.pdf");

        Assert.Contains("Expected salary (per year): EUR 75000", prompt);
        Assert.Contains("That salary is for a job in: Berlin, Germany", prompt);
        Assert.Contains("quote an equivalent amount for that country", prompt);
    }

    [Fact]
    public void With_adjustment_off_the_prompt_asks_only_for_a_currency_conversion()
    {
        var profile = new UserProfile { FullName = "Jane", Email = "j@example.com", ExpectedSalary = "EUR 75000", SalaryBasis = "Berlin, Germany", SalaryAdjust = false };

        var prompt = Prompts.ApplyPrompt(Posting, profile, "resume.pdf");

        Assert.Contains("quote the same amount, only converted", prompt);
        Assert.DoesNotContain("equivalent amount for that country", prompt);
    }

    [Fact]
    public void Without_a_salary_the_prompt_says_nothing_about_one()
    {
        var prompt = Prompts.ApplyPrompt(Posting, new UserProfile { FullName = "Jane", Email = "j@example.com", SalaryBasis = "Berlin" }, "resume.pdf");

        Assert.DoesNotContain("Expected salary", prompt);
        Assert.DoesNotContain("Salary for jobs in other countries", prompt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_apply_instructions_hold_the_salary_rule(bool autoSubmit)
    {
        var rule = Prompts.ApplyInstructions(autoSubmit);

        Assert.Contains("Salary rule", rule);
        Assert.Contains("equivalent amount", rule);
        Assert.Contains("do not ask the user", rule);
    }

    [Fact]
    public void A_profile_saved_before_these_fields_existed_adjusts_by_default_and_the_choice_survives_saving()
    {
        var old = JsonSerializer.Deserialize<UserProfile>("""{ "FullName": "Jane", "ExpectedSalary": "EUR 75000" }""", JsonFile.Options)!;
        Assert.True(old.SalaryAdjust);
        Assert.Equal("", old.SalaryBasis);

        using var env = new TestEnv();
        var service = new ProfileService(env.Paths);
        var controller = new ProfileController(service, null!);
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(new Microsoft.AspNetCore.Http.DefaultHttpContext(), new FakeTempDataProvider());
        controller.Save(new UserProfile { ExpectedSalary = "EUR 75000", SalaryBasis = "Berlin, Germany", SalaryAdjust = false }, null);

        var saved = new ProfileService(env.Paths).Current;
        Assert.Equal("EUR 75000", saved.ExpectedSalary);
        Assert.Equal("Berlin, Germany", saved.SalaryBasis);
        Assert.False(saved.SalaryAdjust);
    }

    private sealed class FakeTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
    }
}
