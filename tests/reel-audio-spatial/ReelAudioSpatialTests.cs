using AlterumModelSwap.Hot;
using Xunit;

namespace AlterumModelSwap.ReelAudioSpatial.Tests;

// The reel's cues are positional on BOTH peers, which means the outcome a client hears comes from
// the settle frame's card type while the host's comes from ReelLogic.Resolve. Both go through
// ReelAudio.TryOutcomeCue, so what is pinned here is that one table: an item card sounds like a win,
// a double like a double, and a refusal cue is never used for an outcome.
//
// The second rule is the fail-safe one: a position that FMOD would reject (NaN/Infinity, which a
// destroyed or not-yet-placed booth can hand over) must be refused before it reaches
// set3DAttributes, so the cue falls back to 2D and stays audible instead of vanishing into an
// ERR_INVALID_VECTOR. Only the float predicate is tested: UnityEngine's Vector3 is a native-backed
// interop type and cannot be constructed outside the game process.
public class ReelAudioSpatialTests
{
    [Fact]
    public void Every_card_type_maps_to_its_own_cue()
    {
        AssertCue(ReelCardType.Item, ReelCue.Win);
        AssertCue(ReelCardType.Double, ReelCue.Double);
        AssertCue(ReelCardType.Damage, ReelCue.Damage);
        AssertCue(ReelCardType.Debuff, ReelCue.Debuff);
        AssertCue(ReelCardType.Blank, ReelCue.Settle);
    }

    [Fact]
    public void An_unknown_card_type_is_refused_rather_than_guessed()
    {
        // A settle frame from a different build: stay silent at this booth instead of playing some
        // other card's cue. The out value only exists to satisfy the signature.
        Assert.False(ReelAudio.TryOutcomeCue(200, out _));
        Assert.False(ReelAudio.TryOutcomeCue(5, out _));            // one past Blank
        Assert.True(ReelAudio.TryOutcomeCue((byte)ReelCardType.Blank, out _));
    }

    [Fact]
    public void A_booth_position_is_usable()
    {
        // A measured pose from the map logs: a booth's sign face is a plain world position.
        Assert.True(ReelAudio.Finite(-34.35f, 1.60f, -4.31f));
        Assert.True(ReelAudio.Finite(0f, 0f, 0f));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(0f, 0f, float.PositiveInfinity)]
    public void A_non_finite_position_is_not_handed_to_fmod(float x, float y, float z)
    {
        Assert.False(ReelAudio.Finite(x, y, z));
    }

    [Theory]
    [InlineData("crucifix", "Crucifix")]
    [InlineData("CRUCIFIX", "")]
    [InlineData("unknown", "Сrucifix")]
    public void Crucifix_is_a_quest_item_and_excluded_from_the_deck(string id, string name)
    {
        Assert.Equal("quest-crucifix", ReelExclusions.ReasonFor(id, name));
        var item = ReelPool.All[ReelPool.IndexOf("crucifix")];
        Assert.False(item.Usable);
        Assert.True(item.Excluded);
        Assert.Equal(0, item.Weight);
    }

    [Fact]
    public void Other_items_and_existing_exclusions_are_unchanged()
    {
        Assert.Equal("quest-rune", ReelExclusions.ReasonFor("rune", "Curse Dispelling Rune"));
        Assert.Equal("quest-lantern", ReelExclusions.ReasonFor("lantern", "Lantern"));
        Assert.Equal("", ReelExclusions.ReasonFor("healthsyringe", "Health Syringe"));
        Assert.True(ReelPool.All[ReelPool.IndexOf("healthsyringe")].Usable);
    }

    private static void AssertCue(ReelCardType type, ReelCue expected)
    {
        Assert.True(ReelAudio.TryOutcomeCue((byte)type, out var cue));
        Assert.Equal(expected, cue);
        // An outcome is a card's voice, never the machine's refusal: those are separate entry points
        // (ReelLogic.TryBeginPull / a verdict) and must not be repurposed as a result sound.
        Assert.NotEqual(ReelCue.Error, cue);
    }
}
