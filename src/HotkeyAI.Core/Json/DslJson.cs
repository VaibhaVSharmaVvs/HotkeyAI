using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotkeyAI.Core.Json;

/// <summary>
/// The canonical serializer settings for automation documents.
/// </summary>
/// <remarks>
/// These are part of the contract, not a caller preference: an automation serialized with
/// different settings can stop validating against the schema. In particular, omitting nulls
/// is required — the schema types every optional property (<c>timeoutMs</c> is an integer,
/// <c>expect</c> is an object), so writing <c>"timeoutMs": null</c> produces a document that
/// fails validation even though the model is perfectly valid. Every component that reads or
/// writes a plan uses this.
/// </remarks>
public static class DslJson
{
    /// <summary>Settings for reading and writing automation documents.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>Same settings, formatted for writing files a human will read and diff.</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) =>
        new()
        {
            // Every property carries an explicit [JsonPropertyName], so no naming policy is
            // applied — the wire names come from the schema and nowhere else.
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,

            // Load-bearing: see the remarks above.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false,
        };
}
