using System.Text.RegularExpressions;

namespace JobApplier.Mail;

/// <summary>What an employer's mail says. <paramref name="Confident"/> is false when the wording pointed two ways.</summary>
public sealed record ReplyVerdict(ReplyKind Kind, bool Confident, string Reason);

/// <summary>
/// Reads an employer's mail and says whether it is an offer, a rejection, an invitation to go on, a request for something, or
/// just an acknowledgement. It is a set of phrase rules in English and Spanish and runs entirely on this PC: no mail text is sent to
/// any AI. It is deliberately cautious - when nothing matches it says <see cref="ReplyKind.Unclear"/> instead of guessing, and
/// when a rejection and an invitation both appear it prefers the rejection and marks the result as not confident.
/// </summary>
public static class ReplyClassifier
{
    private const RegexOptions Ro = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static Regex[] Rules(params string[] patterns) => patterns.Select(p => new Regex(p, Ro)).ToArray();

    // Every pattern runs on text that is lower case, accent-free and keeps its punctuation (see MailText.Prepare).

    private static readonly Regex[] Rejection = Rules(
        @"\bunfortunately\b[^.!?\n]{0,160}\b(not|unable|decided|decision|other candidates|filled|selected|proceed|move forward|moving forward|continue|pursue|progress)\b",
        @"\b(regret|sorry|sad|disappointed) to (inform|tell|let you know|share|say|advise)\b",
        // "decided to move forward with other candidates" is a rejection; "decided to move forward with your application" is not.
        @"\b(decided|opted|chosen|chose|selected) to (move|proceed|go|continue|progress|pursue)( forward)? with (other|another|different|a different|more suitable|someone|candidates|applicants|profiles)\b",
        @"\b(decided|opted|chosen|chose) not to (move|proceed|go|continue|progress|pursue|take|offer)\b",
        @"\b(cannot|can't|unable to|won't|will not) (be )?(move|moving|proceed|proceeding|continue|continuing) forward\b",
        @"\bgo(ing)? (in )?a different direction\b|\bpursue (other|another|different) (candidates?|applicants?|profiles?|direction)\b",
        @"\b(won't|will not|cannot|can't) be able to (offer|proceed|move|continue|progress)\b",
        @"\bwe(?: have|'ve) (already )?(filled|closed) (the|this|that) (position|role|vacancy)\b",
        @"\b(will|would|can|could|are|is) not (be )?(moving|proceeding|going|taking|progressing|continuing|pursuing|able to (move|proceed|continue|offer|progress))\b",
        @"\b(won't|wont|cannot|can't|couldn't|unable to) (be )?(moving|proceed(ing)?|go(ing)?|take|taking|continu(e|ing)|progress(ing)?|offer(ing)?|move)( forward)?( with)? (your|you|the application|any further)",
        @"\bnot (be )?(moving|proceeding|going|progressing|continuing)( forward)? with (your|you)\b",
        @"\b(have not|haven't|has not|hasn't|were not|was not|wasn't|weren't|not been|not) (been )?(selected|shortlisted|successful|chosen|progressed)\b",
        @"\bposition (has been|was|is now|is) (filled|closed|cancel+ed|on hold)\b",
        @"\bno longer (considering|being considered|moving|available|open|taking|proceeding)\b",
        @"\b(other|another|different) (candidates?|applicants?|profiles?)\b[^.!?\n]{0,80}\b(closely|better|more|best) (match|align|fit|suit)",
        @"\bafter (careful|much|a careful|full|further|extensive) (consideration|review|deliberation|evaluation)\b[^.!?\n]{0,160}\b(not|unable|other|unfortunately|decided)\b",
        @"\b(your )?application (was|has been|is|was not|has not been) (unsuccessful|declined|rejected|not successful|not selected)\b",
        @"\bunsuccessful\b",
        @"\bdo(es)? not (meet|match|fit|align)\b[^.!?\n]{0,60}\b(requirements|criteria|profile|needs)\b",
        // Spanish
        @"\blamentamos\b|\blamentablemente\b|\bsentimos (comunicarte|informarte|decirte|comunicarle|informarle|tener que)\b",
        @"\b(hemos )?decidido (continuar|seguir|avanzar|proseguir) con (otros|otras|otro|otra)\b",
        @"\bno (hemos|ha|han|podemos|vamos a|va a|seguiremos|continuaremos|avanzaremos)\b[^.!?\n]{0,40}\b(podido|sido|seguir|continuar|avanzar|proseguir|seleccionar|contigo|con tu|con su)\b[^.!?\n]{0,60}\b(proceso|candidatura|seleccion|contigo|perfil)",
        @"\bno (has|ha|haz) sido (seleccionad|preseleccionad)[oa]\b",
        @"\b(otros|otras) (candidat[oa]s|perfiles)\b[^.!?\n]{0,80}\b(se ajustan|encajan|mas acord|mejor|adecuad)",
        @"\bno (se )?(ajusta|encaja|cumple)\b[^.!?\n]{0,40}\b(perfil|requisitos)\b",
        @"\bproceso de seleccion (ha )?(finalizado|cerrado|concluido|terminado)\b",
        @"\bhemos cubierto (la|el) (vacante|puesto|posicion)\b");

    private static readonly Regex[] OfferStrong = Rules(
        @"\b(pleased|delighted|happy|thrilled|excited)\b[^.!?\n]{0,40}\b(to )?(offer|extend)\b",
        @"\bwe(?: would|'d) like to (offer|extend)\b|\bwe are (offering|extending)\b|\bextend (you )?an offer\b[^.!?\n]{0,40}\b(pleased|happy|delighted)\b",
        @"\b(enhorabuena|felicidades|felicitaciones)\b[^.!?\n]{0,120}\b(oferta|seleccionad|contratacion|incorporacion)",
        @"\bqueremos (ofrecerte|ofrecerle|proponerte)\b|\bnos complace (ofrecerte|ofrecerle|comunicarte)\b");

    private static readonly Regex[] OfferWeak = Rules(
        @"\boffer (of employment|letter)\b|\bjob offer\b|\bemployment offer\b|\bformal offer\b|\bcarta de oferta\b|\boferta (laboral|de trabajo|de empleo)\b");

    // Clear invitations. A rejection that also contains one of these is genuinely mixed, so it is reported as not confident.
    private static readonly Regex[] MovingStrong = Rules(
        @"\b(invite|inviting|invitation)\b[^.!?\n]{0,80}\b(interview|call|chat|meeting|assessment|conversation|screen|test|challenge)",
        @"\binterview (invitation|invite|request|schedule|scheduling|slot|availability|details|confirmation)\b",
        @"\b(schedule|arrange|set up|book|organi[sz]e|propose|plan)\b[^.!?\n]{0,40}\b(call|interview|chat|meeting|conversation|screening|introduction|intro|catch-?up)\b",
        @"\b(we|i)(?: would|'d| d)? (like|love) to (speak|talk|chat|meet|connect|learn more|discuss|invite|schedule|have a)\b",
        @"\b(coding|technical|online|take-?home|skills|home|case) (assessment|challenge|test|exercise|assignment|task|study)\b|\b(hackerrank|codility|codesignal|testgorilla|hirevue|karat|devskiller)\b",
        @"\b(you have been|you've been|you are|you're) (shortlisted|short-listed|selected for|invited)\b",
        // "pleased to move forward with your application" - but not "decided not to", which cannot match "decided to"
        @"\b(decided|happy|pleased|glad|delighted|excited|like|want|wish) to (move|proceed|progress|advance|go) forward with (you|your (application|candidacy|profile))\b",
        @"(?<!(not|n't|never|unable to|no longer) (be |to )?)\b(advance|advanced|advancing|progress|progressed|moved?|moving) (you |your (application|candidacy) )?to the (next|second|third|final|interview|following) (stage|round|step|phase|part)\b",
        // Spanish
        @"\bvideollamada\b|\bprueba tecnica\b|\bprueba de nivel\b|\bagendar\b|\bsiguiente (fase|etapa|ronda)\b|\bproxima (fase|etapa)\b|\bpasar a la siguiente\b",
        @"\bavanzar (en|con) (el|tu|su) proceso\b|\bnos gustaria (hablar|conocerte|contactar|que|invitarte|proponerte)\b|\bhas sido (seleccionad|preseleccionad)[oa]\b");

    // Interview talk. It also turns up in rejections ("after your final interview"), so it only counts when nothing says "no".
    private static readonly Regex[] MovingInterview = Rules(
        @"\b(phone|video|technical|screening|initial|first|second|final|onsite|on-site|recruiter|cultural|panel|intro(ductory)?) (screen|screening|interview|call|round|conversation|chat)\b",
        @"\bavailab(le|ility)\b[^.!?\n]{0,60}\b(call|interview|chat|meeting|next week|this week|slot)\b",
        // Spanish
        @"\bentrevista\b|\bllamada\b");

    // Vague wording. Automatic receipts say "we will be in touch about next steps" too, so it does not count when the mail reads as a receipt.
    private static readonly Regex[] MovingVague = Rules(
        @"\bnext (steps?|stage|round|phase)\b|\bnext step in (the|our) (process|hiring)\b",
        // "move forward", but not "not be moving forward", "unable to proceed" or "move forward with other candidates"
        @"(?<!(not|n't|never|no longer|unable to|cannot|can't) (be |to )?)\b(move|moving|proceed|proceeding|progress|progressing|advance|advancing)( you| your (application|candidacy))?( forward| to the next| to next| on to| to the)\b(?! with (other|another|different|more))",
        // Spanish
        @"\bsiguientes pasos\b|\bsiguiente paso\b");

    // What a rejection says when it is only a possibility: "if you are unsuccessful we will keep your CV on file".
    private static readonly Regex RejectionConditional = new(
        @"[^.!?\n]*\b(if|should|in the event|in case|unless)\b[^,.!?\n]*?\b(unsuccessful|not (be )?(selected|shortlisted|successful|chosen)|not (be )?(moving|proceeding)|not (a )?(match|fit)|(is|are|be) rejected)\b[^.!?\n]*[.!?]?" +
        @"|[^.!?\n]*\bsi\b[^,.!?\n]*?\b(no (has|ha|resultas|eres) (sido )?(seleccionad\w*|preseleccionad\w*)|no encaja|no cumple)\b[^.!?\n]*[.!?]?",
        Ro);

    private static readonly Regex[] Info = Rules(
        @"\bplease (provide|send|submit|complete|upload|fill|confirm|share|attach|verify|finish|answer)\b",
        @"\b(additional|further|more) (information|details|documents?|documentation|questions)\b",
        @"\bcomplete (your|the) (profile|application|form|questionnaire|registration|assessment)\b",
        @"\bwe (need|require)\b[^.!?\n]{0,60}\b(from you|your|additional|information|documents?)\b",
        // Spanish
        @"\bpor favor[^.!?\n]{0,30}\b(envia|enviame|envianos|completa|rellena|responde|indica|confirma|adjunta|facilita|proporciona|comparte)\b|\bnecesitamos\b|\binformacion adicional\b|\bcompleta (tu|el) (perfil|formulario|cuestionario)\b");

    private static readonly Regex[] Acknowledgement = Rules(
        @"\b(we|they)? ?(have )?(received|got) your (application|cv|resume|candidature|submission|details)\b",
        @"\bthank(s| you)( so much| very much)? for (applying|your application|your interest|submitting|your submission|taking the time|considering|showing)\b",
        @"\b(application|candidacy|cv|resume) (has been |was |is |successfully )?(received|submitted|sent|in review|under review|being reviewed|registered)\b",
        @"\b(will|we'll|we will) (carefully )?(review|be in touch|get back|contact you|keep you|reach out)\b",
        @"\bunder review\b|\bbeing reviewed\b|\bapplication confirmation\b|\bconfirmation of (your )?application\b",
        // Spanish
        @"\bhemos recibido (tu|su) (candidatura|solicitud|cv|curriculum|aplicacion)\b|\bgracias por (tu interes|postularte|aplicar|presentar|tu candidatura|enviar|inscribirte|tu solicitud)\b|\b(tu|su) candidatura (ha sido |fue )?(recibida|registrada|enviada)\b|\bestamos (revisando|analizando|evaluando)\b|\ben revision\b");

    // A sentence that makes a good outcome conditional on the candidate being picked ("if you are selected we will schedule an
    // interview", "should your profile match") promises nothing. The condition has to be in the "if" clause itself (before the
    // comma): "if you are available, we would like to schedule a call" is an invitation, not a conditional.
    private static readonly Regex Conditional = new(
        @"[^.!?\n]*\b(if|should|in case|once|whenever)\b[^,.!?\n]*?\b(selected|shortlisted|match(es|ed)?|suitable|(a )?(good |strong |great |right |potential )*fit|appropriate|relevant|qualif\w*|successful|of interest|(think|feel|believe|consider)(s)? (that )?(you|your)|interested in (you|your|moving|taking)|decide[sd]? to (proceed|move|invite|schedule|contact|go|take|progress))\b[^.!?\n]*[.!?]?" +
        @"|[^.!?\n]*\bsi\b[^,.!?\n]*?\b(perfil|encaja|ajusta|seleccionad\w*|preseleccionad\w*|interesa|decidimos|avanzamos|procede|resultas|cumple|adecuad\w*)\b[^.!?\n]*[.!?]?",
        Ro);

    /// <summary>Classifies a message from its subject and its text.</summary>
    public static ReplyVerdict Classify(string? subject, string? body)
    {
        var text = MailText.Prepare((subject ?? "") + ".\n" + (body ?? ""));
        if (text.Length < 8) return new ReplyVerdict(ReplyKind.Unclear, false, "");

        // Good news must not come from a conditional sentence ("if you are selected..."), and bad news must not either
        // ("if you are unsuccessful..."); a sentence that states the outcome plainly counts as it stands.
        var firm = Conditional.Replace(text, " ");
        var plainBad = RejectionConditional.Replace(text, " ");

        var rejection = First(Rejection, plainBad);
        var offerStrong = First(OfferStrong, firm);
        var offerWeak = First(OfferWeak, firm);
        var strong = First(MovingStrong, firm);

        if (rejection != null && offerStrong == null)
            return new ReplyVerdict(ReplyKind.Rejected, Confident: strong == null, Reason: rejection);

        if (offerStrong != null || (offerWeak != null && rejection == null))
            return new ReplyVerdict(ReplyKind.Offer, Confident: true, Reason: offerStrong ?? offerWeak!);

        if ((strong ?? First(MovingInterview, firm)) is { } moving)
            return new ReplyVerdict(ReplyKind.MovingForward, Confident: true, Reason: moving);
        if (First(Info, firm) is { } info) return new ReplyVerdict(ReplyKind.InfoRequested, Confident: true, Reason: info);

        var ack = First(Acknowledgement, text);
        if (ack == null && First(MovingVague, firm) is { } vague)
            return new ReplyVerdict(ReplyKind.MovingForward, Confident: true, Reason: vague);
        if (ack != null) return new ReplyVerdict(ReplyKind.Acknowledged, Confident: true, Reason: ack);

        return new ReplyVerdict(ReplyKind.Unclear, false, "");
    }

    /// <summary>The first phrase that matches, trimmed for display, or null.</summary>
    private static string? First(Regex[] rules, string text)
    {
        foreach (var rule in rules)
        {
            var match = rule.Match(text);
            if (!match.Success) continue;

            var phrase = Regex.Replace(match.Value, @"\s+", " ").Trim();
            return phrase.Length <= 90 ? phrase : phrase[..90] + "...";
        }
        return null;
    }
}
