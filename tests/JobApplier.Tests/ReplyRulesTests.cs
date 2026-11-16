using JobApplier.Domain;
using JobApplier.Mail;

namespace JobApplier.Tests;

public class ReplyClassifierTests
{
    [Theory]
    // rejections
    [InlineData("Update on your application", "Unfortunately, we have decided not to move forward with your application.", ReplyKind.Rejected)]
    [InlineData("Your application", "After careful consideration we have decided to move forward with other candidates.", ReplyKind.Rejected)]
    [InlineData("Your application", "We regret to inform you that the position has been filled.", ReplyKind.Rejected)]
    [InlineData("Application status", "Your application was unsuccessful on this occasion.", ReplyKind.Rejected)]
    [InlineData("Application status", "We will not be moving forward with your candidacy.", ReplyKind.Rejected)]
    [InlineData("Your application", "We are unable to proceed with your application at this time.", ReplyKind.Rejected)]
    [InlineData("Tu candidatura", "Lamentablemente hemos decidido continuar con otros candidatos.", ReplyKind.Rejected)]
    // good news
    [InlineData("Interview invitation", "We would like to invite you to a video interview. Please pick a slot.", ReplyKind.MovingForward)]
    [InlineData("Next steps", "Thanks for your application. Could we schedule a call this week?", ReplyKind.MovingForward)]
    [InlineData("Your application", "We have decided to move forward with your application and would like to schedule a call.", ReplyKind.MovingForward)]
    [InlineData("Coding challenge", "Please complete the take-home assessment within 5 days.", ReplyKind.MovingForward)]
    [InlineData("Tu candidatura", "Nos gustaría hablar contigo. ¿Podemos agendar una videollamada?", ReplyKind.MovingForward)]
    // offers
    [InlineData("Offer", "We are pleased to offer you the position of Software Engineer.", ReplyKind.Offer)]
    [InlineData("Enhorabuena", "Felicidades, queremos ofrecerte el puesto. Adjuntamos la oferta laboral.", ReplyKind.Offer)]
    // asks
    [InlineData("More details", "Please complete your profile and upload your CV so we can continue.", ReplyKind.InfoRequested)]
    // automatic receipts
    [InlineData("Application received", "Thank you for applying to Acme. We have received your application.", ReplyKind.Acknowledged)]
    [InlineData("Tu candidatura", "Hemos recibido tu candidatura. Gracias por tu interés.", ReplyKind.Acknowledged)]
    // nothing about an application
    [InlineData("Weekly digest", "10 new jobs for you this week, picked from what you searched.", ReplyKind.Unclear)]
    public void A_message_is_read_the_way_a_person_would_read_it(string subject, string body, ReplyKind expected)
    {
        Assert.Equal(expected, ReplyClassifier.Classify(subject, body).Kind);
    }

    [Theory]
    [InlineData("Your application", "We've decided to go in a different direction for this role.", ReplyKind.Rejected)]
    [InlineData("Your application", "Unfortunately we will not be moving forward with your application, and if you would like feedback, just reply.", ReplyKind.Rejected)]
    [InlineData("Your application", "We'd like to offer you the role of Backend Engineer.", ReplyKind.Offer)]
    [InlineData("Application received", "Thanks for applying! We have decided to move forward with your application.", ReplyKind.MovingForward)]
    [InlineData("Application received", "Thanks for applying. Your phone screen with Jane is on Thursday at 3pm.", ReplyKind.MovingForward)]
    // automatic receipts that talk about what might come later
    [InlineData("Application received", "Thank you for applying. We will review it and be in touch about next steps.", ReplyKind.Acknowledged)]
    [InlineData("Application received", "We have received your application. If we think you are a good fit, we will invite you to an interview.", ReplyKind.Acknowledged)]
    [InlineData("Application received", "We have received your application. If you are unsuccessful we will keep your CV on file for a year.", ReplyKind.Acknowledged)]
    [InlineData("Application received", "We have received your application. Should you not be selected, you will not hear back from us.", ReplyKind.Acknowledged)]
    public void Wording_that_sounds_like_the_opposite_is_read_for_what_it_means(string subject, string body, ReplyKind expected)
    {
        Assert.Equal(expected, ReplyClassifier.Classify(subject, body).Kind);
    }

    [Fact]
    public void Next_steps_without_any_receipt_wording_is_still_good_news()
    {
        Assert.Equal(ReplyKind.MovingForward, ReplyClassifier.Classify("Next steps", "Here are the next steps in the process.").Kind);
    }

    [Fact]
    public void A_request_for_documents_under_next_steps_is_a_request()
    {
        Assert.Equal(ReplyKind.InfoRequested, ReplyClassifier.Classify("Next steps", "Next steps: please upload your ID and a copy of your degree.").Kind);
    }

    [Theory]
    [InlineData("If you are selected for an interview, we will contact you within two weeks.")]
    [InlineData("Should your profile match, we will invite you to an interview.")]
    [InlineData("Si tu perfil encaja, te contactaremos para agendar una entrevista.")]
    public void A_promise_that_depends_on_being_picked_is_not_good_news(string conditional)
    {
        var verdict = ReplyClassifier.Classify("Application received", "We have received your application. " + conditional);

        Assert.Equal(ReplyKind.Acknowledged, verdict.Kind);
    }

    [Fact]
    public void An_offer_that_depends_on_something_is_not_an_offer()
    {
        var verdict = ReplyClassifier.Classify("Thanks for applying", "Thanks for applying. Should your profile match, we would be happy to extend an offer.");

        Assert.NotEqual(ReplyKind.Offer, verdict.Kind);
    }

    [Fact]
    public void An_invitation_that_only_asks_about_availability_is_still_an_invitation()
    {
        var verdict = ReplyClassifier.Classify("Chat?", "If you are available on Thursday, we would like to schedule a call with the team.");

        Assert.Equal(ReplyKind.MovingForward, verdict.Kind);
    }

    [Fact]
    public void A_rejection_wins_over_polite_talk_of_next_steps_and_stays_confident()
    {
        var verdict = ReplyClassifier.Classify("Your application", "Unfortunately we won't be moving forward with your application. We wish you all the best in your next steps.");

        Assert.Equal(ReplyKind.Rejected, verdict.Kind);
        Assert.True(verdict.Confident);
    }

    [Fact]
    public void A_rejection_that_also_invites_a_call_is_reported_but_not_as_certain()
    {
        var verdict = ReplyClassifier.Classify("Your application", "Unfortunately we will not be moving forward with your application. We would like to schedule a call to share some feedback.");

        Assert.Equal(ReplyKind.Rejected, verdict.Kind);
        Assert.False(verdict.Confident);
    }

    [Fact]
    public void The_phrase_that_decided_is_kept_so_the_result_can_be_checked()
    {
        var verdict = ReplyClassifier.Classify("Your application", "We regret to inform you that we are not proceeding.");

        Assert.Contains("regret to inform", verdict.Reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("Hi", "ok")]
    public void Nothing_to_read_is_unclear_not_an_error(string? subject, string? body)
    {
        Assert.Equal(ReplyKind.Unclear, ReplyClassifier.Classify(subject, body).Kind);
    }
}

public class ReplyMatcherTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationRecord App(string company, string role = "Software Engineer", string url = "https://jobs.example.org/1", int daysAgo = 5) =>
        new() { Company = company, Role = role, Url = url, JobId = Guid.NewGuid(), AppliedUtc = Now.AddDays(-daysAgo) };

    private static MailMessage Mail(string from, string subject, string snippet = "", int daysAgo = 1, params string[] labels)
    {
        var (name, address) = GmailApi.ParseAddress(from);
        return new MailMessage(Guid.NewGuid().ToString("N"), "thread", name, address, subject, Now.AddDays(-daysAgo), snippet, labels);
    }

    private static ReplyMatch? Match(MailMessage mail, params ApplicationRecord[] apps) => ReplyMatcher.Match(mail, apps, "me@gmail.com", useBody: false);

    [Fact]
    public void Mail_from_the_company_about_the_application_is_matched()
    {
        var app = App("Acme Corp");

        var match = Match(Mail("Acme Careers <careers@acme.com>", "Your application for Software Engineer"), app);

        Assert.NotNull(match);
        Assert.Same(app, match!.Application);
        Assert.True(match.Strength >= 5);
        Assert.True(match.CompanyInHeader);
    }

    [Fact]
    public void Mail_sent_through_a_recruiting_system_is_matched_by_the_company_in_the_subject()
    {
        var app = App("Acme Corp");

        Assert.NotNull(Match(Mail("no-reply@greenhouse.io", "Thank you for applying to Acme"), app));
    }

    [Fact]
    public void A_recruiting_system_domain_alone_proves_nothing()
    {
        var app = App("Acme Corp");

        Assert.Null(Match(Mail("no-reply@greenhouse.io", "Thank you for applying to Globex"), app));
    }

    [Fact]
    public void Another_companys_application_mail_is_not_matched()
    {
        Assert.Null(Match(Mail("Globex Jobs <jobs@globex.com>", "Your application"), App("Acme Corp")));
    }

    [Theory]
    [InlineData("CATEGORY_PROMOTIONS")]
    [InlineData("CATEGORY_SOCIAL")]
    [InlineData("SENT")]
    [InlineData("DRAFT")]
    public void Promotions_social_mail_and_the_users_own_mail_are_skipped(string label)
    {
        Assert.Null(Match(Mail("Acme <careers@acme.com>", "Your application at Acme", labels: label), App("Acme Corp")));
    }

    [Fact]
    public void Mail_the_user_wrote_themselves_is_skipped()
    {
        Assert.Null(Match(Mail("Me <me@gmail.com>", "Application to Acme"), App("Acme Corp")));
    }

    [Fact]
    public void Mail_from_before_the_application_cannot_be_a_reply()
    {
        var app = App("Acme Corp", daysAgo: 2);

        Assert.Null(Match(Mail("Acme <careers@acme.com>", "Your application", daysAgo: 6), app));
    }

    [Fact]
    public void A_one_word_company_that_is_an_ordinary_word_does_not_count_when_it_is_only_in_the_text()
    {
        var app = App("Talent");

        var match = ReplyMatcher.Match(Mail("hr@unrelated.com", "Update on your application", "Talent matters to us"), new[] { app }, "me@gmail.com", useBody: false);

        Assert.Null(match);
    }

    [Fact]
    public void With_several_applications_to_one_company_the_role_decides()
    {
        var data = App("Acme Corp", "Data Scientist");
        var backend = App("Acme Corp", "Backend Engineer");

        var match = Match(Mail("Acme <careers@acme.com>", "Backend Engineer - interview"), data, backend);

        Assert.Same(backend, match!.Application);
    }

    [Fact]
    public void Legal_suffixes_and_generic_endings_do_not_stop_a_match()
    {
        var app = App("Vortech PCI Group Ltd");

        Assert.NotNull(Match(Mail("Vortech PCI <jobs@vortechpci.io>", "Application received"), app));
    }

    [Fact]
    public void The_company_can_be_recognised_from_the_senders_domain()
    {
        var app = App("Wolters Kluwer");

        Assert.NotNull(Match(Mail("jobs@wolterskluwer.com", "Your application"), app));
    }

    [Fact]
    public void Mail_from_the_domain_the_application_was_made_on_is_matched()
    {
        var app = App("Hooli", url: "https://jobs.xyzcorp.com/careers/4821");

        Assert.NotNull(Match(Mail("recruiting@mail.xyzcorp.com", "Next steps for your application"), app));
    }

    [Fact]
    public void A_company_in_the_text_only_is_not_in_the_header()
    {
        var app = App("Globex Industries");

        var match = ReplyMatcher.Match(Mail("alerts@jobboard.example", "New jobs for you", "Globex Industries is hiring a Software Engineer"), new[] { app }, "me@gmail.com", useBody: false);

        Assert.NotNull(match);
        Assert.False(match!.CompanyInHeader);
    }

    [Fact]
    public void Mail_that_could_be_about_an_application_is_worth_reading_in_full_and_a_sale_is_not()
    {
        Assert.True(ReplyMatcher.LooksLikeApplicationMail(Mail("x@y.com", "Interview with our team")));
        Assert.True(ReplyMatcher.LooksLikeApplicationMail(Mail("no-reply@greenhouse.io", "Hello")));
        Assert.False(ReplyMatcher.LooksLikeApplicationMail(Mail("shop@store.com", "Summer sale: 50% off shoes")));
    }
}
