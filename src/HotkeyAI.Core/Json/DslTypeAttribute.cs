namespace HotkeyAI.Core.Json;

/// <summary>
/// Maps a concrete DSL type to its <c>type</c> discriminator.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately replaces <c>[JsonDerivedType]</c>. That attribute switches on
/// System.Text.Json's built-in polymorphism, which requires the discriminator to be the first
/// property in the object and refuses to coexist with a custom converter — the two together
/// throw "does not support metadata writes or reads". Since plans are hand-edited and
/// machine-generated and JSON property order means nothing, we need order-independent
/// dispatch, so we own the registry instead of borrowing STJ's.
/// </para>
/// <para>
/// One declaration, two readers: <see cref="DiscriminatedJsonConverter{TBase}"/> uses it to
/// dispatch, and the schema conformance test uses it to check the C# hierarchy against the
/// schema's <c>oneOf</c> in both directions.
/// </para>
/// </remarks>
/// <param name="derivedType">The concrete record.</param>
/// <param name="discriminator">Its <c>type</c> value in the schema.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DslTypeAttribute(Type derivedType, string discriminator) : Attribute
{
    public Type DerivedType { get; } = derivedType;

    public string Discriminator { get; } = discriminator;
}
