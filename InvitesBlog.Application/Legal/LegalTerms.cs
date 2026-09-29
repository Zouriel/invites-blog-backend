namespace InvitesBlog.Application.Legal;

/// <summary>
/// The version of the Terms, Refund and Privacy pages a buyer accepts at checkout. The site sends the
/// version it showed; a payment is refused if that isn't this one, so nobody pays under terms they
/// never saw. Change it together with the site's <c>TERMS_VERSION</c> whenever those pages change in a
/// way that matters to someone paying.
/// </summary>
public static class LegalTerms
{
    public const string Version = "2026-09-29";
}
