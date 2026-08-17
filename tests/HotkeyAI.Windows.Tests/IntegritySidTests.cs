using HotkeyAI.Windows;

namespace HotkeyAI.Windows.Tests;

/// <summary>
/// Reading an integrity level out of a SID whose sub-authority count makes no sense.
/// </summary>
/// <remarks>
/// <c>LevelOf</c> used to compute
/// <c>8 + ((subAuthorities - 1) * 4)</c> inline and unguarded. A count of zero gives offset 4, four
/// bytes out of the middle of the SID's own header — an identifier-authority fragment returned as
/// though it were an integrity level. A count above fifteen, Win32's maximum, reads past the end of
/// the structure.
/// <para>
/// Neither can happen for a SID the kernel wrote, which is why it survived: reaching it means
/// memory that is already wrong. The value it produced was the problem — a plausible-looking
/// integer that <c>IsHigherThanUs</c> would compare against High and answer confidently.
/// </para>
/// </remarks>
public sealed class IntegritySidTests
{
    [Theory]
    [InlineData((byte)1, 8)]     // one sub-authority: the first slot, right after the header
    [InlineData((byte)2, 12)]
    [InlineData((byte)3, 16)]    // the ordinary shape for a mandatory label
    [InlineData((byte)15, 64)]   // the most a SID may carry
    public void ASensibleCountGivesTheLastSubAuthoritysOffset(byte count, int expected)
    {
        Assert.Equal(expected, Integrity.SubAuthorityOffset(count));
    }

    [Fact]
    public void ZeroIsRefusedRatherThanReadingBackwardsIntoTheHeader()
    {
        // The bug: 8 + (-1 * 4) = 4, which is inside SID.IdentifierAuthority.
        Assert.Equal(-1, Integrity.SubAuthorityOffset(0));
    }

    [Theory]
    [InlineData((byte)16)]
    [InlineData((byte)100)]
    [InlineData((byte)255)]
    public void MoreThanFifteenIsRefusedRatherThanReadingPastTheEnd(byte count)
    {
        Assert.Equal(-1, Integrity.SubAuthorityOffset(count));
    }

    [Fact]
    public void NoValidCountLandsInsideTheHeader()
    {
        // The header is eight bytes: revision, sub-authority count, and six of identifier
        // authority. Any offset the function accepts must be at or past the end of it, or the value
        // read is not a sub-authority at all.
        for (var count = 0; count <= 255; count++)
        {
            var offset = Integrity.SubAuthorityOffset((byte)count);

            Assert.True(
                offset == -1 || offset >= 8,
                $"count {count} produced offset {offset}, which is inside the SID header");
        }
    }
}
