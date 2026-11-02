using System.Text;
using InvitesBlog.Application.Exceptions;

namespace InvitesBlog.Application.Moderation;

/// <summary>
/// The basic filter in front of comments and post captions: refuses obvious slurs and direct threats
/// with a plain message, before anyone else sees them. Reports and the admin queue catch the rest.
///
/// <para><b>How it matches.</b> Words, not substrings: the text is lower-cased, common look-alike
/// digits and symbols are read as letters (0→o, 1→i, 3→e, 4→a, 5→s, 7→t, @→a, $→s), and anything
/// else that isn't a letter splits words. A single-word term must equal a whole word, so "Scunthorpe"
/// or "class" never trip it; a phrase must appear as those words in a row.</para>
///
/// <para>The list lives in <see cref="ObjectionableTerms"/>. Add to it there, with a test.</para>
/// </summary>
public static class ObjectionableText
{
    public const string Code = "objectionable_text";
    public const string Refusal =
        "That includes words we don't allow on invites.blog. Please change it and try again.";

    private static readonly HashSet<string> Words =
        ObjectionableTerms.Words.Select(Normalise).ToHashSet(StringComparer.Ordinal);

    private static readonly string[] Phrases =
        ObjectionableTerms.Phrases.Select(p => " " + string.Join(' ', Tokens(p)) + " ").ToArray();

    /// <summary>Whether the text contains a listed word or phrase.</summary>
    public static bool IsObjectionable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = Tokens(text);
        if (tokens.Count == 0) return false;
        if (tokens.Any(Words.Contains)) return true;
        var joined = " " + string.Join(' ', tokens) + " ";
        return Phrases.Any(p => joined.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Throws the refusal users see when <see cref="IsObjectionable"/> says yes.</summary>
    public static void Check(string? text)
    {
        if (IsObjectionable(text)) throw new BusinessRuleException(Refusal, Code);
    }

    private static List<string> Tokens(string text)
    {
        var normal = Normalise(text);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in normal)
        {
            if (char.IsLetter(c)) current.Append(c);
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            sb.Append(raw switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                _ => raw,
            });
        return sb.ToString();
    }
}
