using System;
using AlterumModelSwap;
using Xunit;

namespace AlterumModelSwap.InstanceLabeling.Tests;

// A resolution change makes Unity destroy and recreate its window: for a moment there is no
// UnityWndClass window (or a replacement is still being created), then exactly one appears again.
// These tests pin the policy that lets that happen without the guard killing the game.
public class WindowReacquirePolicyTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    [Fact]
    public void TransientAbsenceWithinGrace_WaitsInsteadOfFailing()
    {
        var decision = WindowReacquirePolicy.Decide(0, TimeSpan.FromSeconds(2), Grace);

        Assert.Equal(WindowReacquirePolicy.Decision.Wait, decision);
    }

    [Fact]
    public void AbsenceAfterGrace_IsFatal()
    {
        var decision = WindowReacquirePolicy.Decide(0, Grace, Grace);

        Assert.Equal(WindowReacquirePolicy.Decision.Fatal, decision);
    }

    [Fact]
    public void ReplacementWindow_IsReacquiredImmediately()
    {
        var decision = WindowReacquirePolicy.Decide(1, TimeSpan.FromSeconds(1), Grace);

        Assert.Equal(WindowReacquirePolicy.Decision.Reacquire, decision);
    }

    [Fact]
    public void AmbiguousWindows_WaitThenFail()
    {
        Assert.Equal(WindowReacquirePolicy.Decision.Wait,
            WindowReacquirePolicy.Decide(2, TimeSpan.FromSeconds(1), Grace));
        Assert.Equal(WindowReacquirePolicy.Decision.Fatal,
            WindowReacquirePolicy.Decide(2, Grace, Grace));
    }

    [Fact]
    public void ResolutionChange_SurvivesWhenTheNewWindowArrivesInsideTheGracePeriod()
    {
        // Old window gone (0 matches) for a few frames...
        Assert.Equal(WindowReacquirePolicy.Decision.Wait,
            WindowReacquirePolicy.Decide(0, TimeSpan.FromMilliseconds(250), Grace));
        Assert.Equal(WindowReacquirePolicy.Decision.Wait,
            WindowReacquirePolicy.Decide(0, TimeSpan.FromSeconds(3), Grace));

        // ...then the recreated window appears: re-acquire, never fatal.
        Assert.Equal(WindowReacquirePolicy.Decision.Reacquire,
            WindowReacquirePolicy.Decide(1, TimeSpan.FromSeconds(3), Grace));
    }

    [Fact]
    public void SingleMatch_IsAlwaysAuthoritativeEvenAfterGrace()
    {
        var decision = WindowReacquirePolicy.Decide(1, TimeSpan.FromMinutes(5), Grace);

        Assert.Equal(WindowReacquirePolicy.Decision.Reacquire, decision);
    }
}
