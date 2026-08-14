using System.Text.Json.Serialization;

namespace HotkeyAI.Core.Dsl;

/// <summary>
/// The on-the-wire spelling of an enum member, as the schema defines it.
/// </summary>
/// <remarks>
/// Error messages and rendered plans must use these, never <c>ToString()</c> or a lower-cased
/// variant. The CLR names differ from the wire names by necessity — <c>PathList</c> against
/// <c>pathList</c>, <c>MediaPlayPause</c> against <c>media_play_pause</c> — and a message
/// telling an author their variable is "pathlist" invites them to write exactly that, which
/// the schema then rejects. An error that misquotes the contract is worse than terse.
/// </remarks>
public static class WireName
{
    /// <summary>The schema's spelling of an enum member.</summary>
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(TEnum).GetField(name);

        return member?.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false)
            is [JsonStringEnumMemberNameAttribute attribute]
            ? attribute.Name
            : name;
    }
}
