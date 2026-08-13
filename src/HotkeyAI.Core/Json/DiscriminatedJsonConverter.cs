using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HotkeyAI.Core.Json;

/// <summary>
/// Polymorphic (de)serialization driven by a <c>type</c> discriminator that may appear
/// anywhere in the object.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json's built-in polymorphism requires the discriminator to be the <i>first</i>
/// property in the JSON object and throws otherwise. That is untenable here: plans are
/// hand-edited and machine-generated, JSON property order carries no meaning, and the schema
/// imposes no ordering — so a plan that validates perfectly would fail to load purely because
/// <c>id</c> was written before <c>type</c>.
/// </para>
/// <para>
/// The <c>[JsonDerivedType]</c> attributes on the base type are still the single declaration
/// of the mapping — this converter reads them rather than duplicating the list, so the
/// conformance test keeps checking the same source it always did.
/// </para>
/// </remarks>
/// <typeparam name="TBase">The abstract base of the hierarchy.</typeparam>
public sealed class DiscriminatedJsonConverter<TBase> : JsonConverter<TBase>
    where TBase : class
{
    private const string Discriminator = "type";

    private static readonly Dictionary<string, Type> ByName = BuildNameMap();
    private static readonly Dictionary<Type, string> ByType =
        ByName.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static Dictionary<string, Type> BuildNameMap()
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var attribute in typeof(TBase)
            .GetCustomAttributes<DslTypeAttribute>(inherit: false))
        {
            map[attribute.Discriminator] = attribute.DerivedType;
        }

        if (map.Count == 0)
        {
            throw new InvalidOperationException(
                $"{typeof(TBase).Name} has no [DslType] attributes to map.");
        }

        return map;
    }

    /// <summary>Handle only the base type; concrete types serialize normally.</summary>
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(TBase);

    public override TBase? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"Expected an object for {typeof(TBase).Name}, got {root.ValueKind}.");
        }

        if (!root.TryGetProperty(Discriminator, out var marker)
            || marker.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"Missing or non-string \"{Discriminator}\" on a {typeof(TBase).Name}.");
        }

        var name = marker.GetString()!;
        if (!ByName.TryGetValue(name, out var concrete))
        {
            throw new JsonException(
                $"Unknown {typeof(TBase).Name} type \"{name}\". Known types: "
                + string.Join(", ", ByName.Keys.Order(StringComparer.Ordinal)));
        }

        // Safe from recursion: CanConvert matches only TBase, and concrete != TBase.
        return (TBase?)root.Deserialize(concrete, options);
    }

    public override void Write(
        Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var concrete = value.GetType();
        if (!ByType.TryGetValue(concrete, out var name))
        {
            throw new JsonException(
                $"{concrete.Name} is not registered with a [DslType] on {typeof(TBase).Name}.");
        }

        var node = JsonSerializer.SerializeToNode(value, concrete, options)
            ?? throw new JsonException($"{concrete.Name} serialized to null.");

        writer.WriteStartObject();

        // Written first for readability, and so round-tripped output stays loadable by any
        // consumer that does expect the discriminator first.
        writer.WriteString(Discriminator, name);

        foreach (var property in node.AsObject())
        {
            if (string.Equals(property.Key, Discriminator, StringComparison.Ordinal))
            {
                continue;
            }

            writer.WritePropertyName(property.Key);

            if (property.Value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
