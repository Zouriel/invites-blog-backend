namespace InvitesBlog.Application.Moderation;

/// <summary>
/// What <see cref="ObjectionableText"/> refuses in comments and post captions. Kept short on purpose:
/// slurs and direct threats that have no innocent use in a comment under someone's wedding photos.
/// Ordinary swearing is not here; people swear when they're happy too. Anything subtler is for
/// reports and the admin queue (docs/MODERATION.md).
///
/// <para><b>Adding a term.</b> Lower case, written plainly (the matcher already reads 4 as a, 0 as o
/// and so on). A single word goes in <see cref="Words"/> and must match a whole word; anything with a
/// space goes in <see cref="Phrases"/>. Check it can't appear inside an ordinary word or name, and add
/// it to the test that lists examples.</para>
///
/// <para>English only for now. Dhivehi terms need a native speaker to choose them.</para>
/// </summary>
public static class ObjectionableTerms
{
    public static readonly string[] Words =
    [
        // Racial and ethnic slurs.
        "nigger", "niggers", "nigga", "niggas", "niggaz", "kike", "kikes", "spic", "spics", "chink", "chinks",
        "gook", "gooks", "wetback", "wetbacks", "paki", "pakis", "raghead", "ragheads", "towelhead",
        "towelheads", "sandnigger", "sandniggers", "beaner", "beaners", "jigaboo", "porchmonkey",
        // Slurs about sexuality and gender.
        "faggot", "faggots", "fag", "fags", "tranny", "trannies", "shemale",
        // Slurs about disability.
        "retard", "retards",
        // Abuse aimed at a person.
        "cunt", "cunts", "whore", "whores", "slut", "sluts", "kys",
    ];

    public static readonly string[] Phrases =
    [
        "kill yourself", "kill urself", "kill your self", "kill ur self", "go die", "hope you die",
        "hope u die", "you should die", "u should die", "i will kill you", "ill kill you",
        "i ll kill you", "im going to kill you", "i am going to kill you", "rape you", "gonna rape",
    ];
}
