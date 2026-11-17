using JobApplier.Insights;

namespace JobApplier.Tests;

public class SkillAtomsTests
{
    [Theory]
    [InlineData("Docker and Kubernetes", "Docker|Kubernetes")]
    [InlineData("DDD, CQRS", "DDD|CQRS")]
    [InlineData("RabbitMQ, Azure Service Bus o Kafka", "RabbitMQ|Azure Service Bus|Kafka")]
    [InlineData("Docker / Kubernetes (not shown)", "Docker|Kubernetes")]
    [InlineData("SQL/NoSQL", "SQL|NoSQL")]
    [InlineData("Node.js y TypeScript", "Node.js|TypeScript")]
    [InlineData("C/C++", "C|C++")]
    [InlineData("CI/CD pipelines", "CI/CD pipelines")]       // one skill: the slash is part of its name
    [InlineData("TCP/IP networking", "TCP/IP networking")]
    [InlineData("Kubernetes (K8s)", "Kubernetes")]            // the bracket is a remark
    public void An_entry_holding_several_skills_is_split_into_them(string entry, string expected)
    {
        Assert.Equal(expected.Split('|'), SkillNormalizer.Atoms(entry));
    }

    [Theory]
    [InlineData("Job description was empty, so specific requirements could not be assessed")]
    [InlineData("R&D")]
    [InlineData("(not shown)")]
    [InlineData("")]
    [InlineData(null)]
    public void A_sentence_a_department_or_nothing_at_all_is_not_a_skill(string? entry)
    {
        Assert.Empty(SkillNormalizer.Atoms(entry));
    }

    [Theory]
    [InlineData("Work authorisation in Spain (sponsorship needed)", true)]
    [InlineData("Right to work in the EU", true)]
    [InlineData("Visa sponsorship", true)]
    [InlineData("Relocation to Madrid", true)]
    [InlineData("Onsite in Barcelona", true)]
    [InlineData("Security clearance", true)]
    [InlineData("Kubernetes", false)]
    [InlineData("Spanish language", false)]     // a language is a skill: it can be stated on a resume when it is true
    [InlineData("Domain-Driven Design", false)]
    public void Requirements_about_the_person_are_told_from_skills(string entry, bool eligibility)
    {
        var key = SkillNormalizer.Key(SkillNormalizer.Atoms(entry)[0]);

        Assert.Equal(eligibility, SkillNormalizer.IsEligibility(key));
    }

    [Fact]
    public void A_compound_entry_counts_for_each_of_its_skills_and_merges_with_the_same_skills_listed_alone()
    {
        var index = SkillIndex.Build(new[] { "Docker and Kubernetes", "Kubernetes", "Docker", "K8s / Docker" });

        var keys = index.KeysOf("Docker and Kubernetes");
        Assert.Equal(2, keys.Count);
        Assert.Contains(index.KeyOf("Kubernetes")!, keys);
        Assert.Contains(index.KeyOf("Docker")!, keys);
        Assert.Equal(index.KeyOf("Kubernetes"), index.KeyOf("K8s"));
        Assert.Equal(2, index.Keys.Count);
    }
}

public class TextLanguageTests
{
    [Theory]
    [InlineData("We are hiring a backend engineer to build services on AWS. You will work with our team and be part of the platform that our customers use every day.", "en")]
    [InlineData("Buscamos un ingeniero para construir microservicios en la nube con C#. Trabajarás con Docker en un equipo colaborativo de la empresa para el desarrollo de nuestros productos.", "es")]
    [InlineData("Wir suchen einen Entwickler für unser Team, der mit uns die Plattform weiterentwickelt und die Services von morgen baut. Sie arbeiten mit Docker und Kubernetes zusammen.", "de")]
    [InlineData("Wij zoeken een ontwikkelaar voor ons team die met ons de platformen van de toekomst bouwt. Je werkt met Docker en Kubernetes en bent een van de mensen die het verschil maakt.", "nl")]
    [InlineData("Nous recherchons un développeur pour notre équipe qui construit avec nous la plateforme de demain et les services que vous utilisez dans votre entreprise.", "fr")]
    public void The_language_of_a_posting_is_recognised(string text, string expected)
    {
        Assert.Equal(expected, TextLanguage.Detect(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C# .NET SQL Docker")]
    [InlineData("Kubernetes Docker Terraform Helm Prometheus Grafana Kafka RabbitMQ Redis Postgres MongoDB Elasticsearch Jenkins Ansible Vagrant Puppet Chef Nginx Envoy")]
    public void Too_little_text_or_a_list_of_technology_names_has_no_language(string? text)
    {
        Assert.Null(TextLanguage.Detect(text));
    }

    [Fact]
    public void Languages_have_names()
    {
        Assert.Equal("German", TextLanguage.Name("de"));
        Assert.Equal("Dutch", TextLanguage.Name("nl"));
        Assert.Equal("xx", TextLanguage.Name("xx"));
    }
}

public class GapAnalyzerRealWorldTests
{
    private const string Spanish =
        "Buscamos un ingeniero backend para construir microservicios en la nube con C# y .NET. Trabajarás con Docker y Kubernetes en un equipo colaborativo " +
        "de la empresa para el desarrollo de nuestros productos y la mejora continua de los servicios que ofrecemos a nuestros clientes.";

    private static GapReport Analyze(IReadOnlyList<AnalyzedJob> jobs, string? resume = null) =>
        GapAnalyzer.Analyze(resume ?? InsightsData.Resume, jobs, new AnalysisOptions(70));

    [Fact]
    public void A_skills_list_entry_with_two_skills_counts_for_both_and_merges_with_jobs_that_list_them_alone()
    {
        var jobs = InsightsData.Corpus().Select((j, i) => i < 6
            ? j with { Missing = new[] { "Docker and Kubernetes" } }     // one entry, two skills
            : j).ToList();

        var report = Analyze(jobs);

        Assert.Equal(12, report.Gaps.Single(g => g.Skill == "Kubernetes").Jobs);   // 6 from the compound entry + 6 that list it alone
        Assert.Contains(report.Gaps, g => g.Skill == "Docker" && g.Jobs == 6);
    }

    [Fact]
    public void Requirements_about_the_person_are_listed_as_gaps_but_never_recommended()
    {
        var jobs = InsightsData.Corpus().Select(j => j with { Missing = j.Missing.Append("Work authorisation in Spain (sponsorship needed)").ToList() }).ToList();

        var report = Analyze(jobs);

        var gap = report.Gaps.Single(g => g.Eligibility);
        Assert.Equal(22, gap.Jobs);
        Assert.False(gap.Addable);
        Assert.DoesNotContain(report.Recommendations, r => r.Skill.Contains("authorisation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Notes, n => n.Contains("requirements about you"));
    }

    [Fact]
    public void Postings_in_another_language_than_the_resume_are_left_out_of_the_text_analysis_and_the_note_says_how_many()
    {
        var jobs = InsightsData.Corpus().Select((j, i) => i is 0 or 1 or 12 ? j with { Text = Spanish } : j).ToList();

        var report = Analyze(jobs);

        Assert.Equal(22, report.JobsWithText);                       // they have text...
        Assert.Contains(report.Notes, n => n.Contains("3 of the 22 posting texts are not in the language of your resume (English): 3 Spanish"));
        Assert.Equal(19, report.Families.Sum(f => f.Jobs));           // ...but only the 19 English ones were grouped
        Assert.All(report.Keywords, k => Assert.DoesNotContain("microservicios", k.Term));
        Assert.Equal(22, report.ScoredJobs);                          // and the skill analysis still counts all 22
        Assert.Equal(12, report.Gaps.Single(g => g.Skill == "Kubernetes").Jobs);
    }

    [Fact]
    public void A_term_that_only_one_employers_postings_use_is_not_a_keyword()
    {
        // every backend posting comes from the same employer (a recruiter repeating one ad); the data postings come from ten different ones
        var jobs = InsightsData.Corpus().Select(j => j.Title == "Backend Engineer" ? j with { Company = "Acme Recruiting" } : j).ToList();

        var terms = Analyze(jobs).Keywords.Select(k => k.Term).ToList();

        Assert.DoesNotContain(terms, t => t.Split(' ').Contains("kubernetes"));
        Assert.DoesNotContain(terms, t => t.Split(' ').Contains("terraform"));
        Assert.Contains(terms, t => t.Split(' ').Any(w => w is "tensorflow" or "pandas" or "python" or "forecasting"));
    }

    [Fact]
    public void A_word_spelled_differently_in_the_resume_is_not_a_keyword_gap()
    {
        var jobs = InsightsData.Corpus();

        var withHyphen = Analyze(jobs, InsightsData.Resume.Replace("Backend", "Back-end"));
        var without = Analyze(jobs, InsightsData.Resume.Replace("Backend", "Engineer"));

        Assert.DoesNotContain(withHyphen.Keywords, k => k.Term.Split(' ').Contains("backend"));
        Assert.Contains(without.Keywords, k => k.Term.Split(' ').Contains("backend"));
    }

    [Fact]
    public void Employer_names_do_not_label_the_families()
    {
        var report = Analyze(InsightsData.Corpus());

        // "Company0", "Lab3"... appear in the posting texts: each belongs to one posting, so none can be the name of a family
        Assert.All(report.Families, f => Assert.DoesNotContain(f.TopTerms, t => t.StartsWith("company", StringComparison.OrdinalIgnoreCase) || t.StartsWith("lab", StringComparison.OrdinalIgnoreCase)));
    }
}
