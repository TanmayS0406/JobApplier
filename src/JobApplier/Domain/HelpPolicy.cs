namespace JobApplier.Domain;

public static class HelpPolicy
{
    /// <summary>
    /// Only the user's answer to a real question on an application form is remembered and reused. One-off confirmations,
    /// CAPTCHAs, verifications and reviews must never end up in the saved answers.
    /// </summary>
    public static bool ShouldRemember(HelpRequest request, string action) =>
        request.Kind == HelpKind.Question && action == "answer";
}
