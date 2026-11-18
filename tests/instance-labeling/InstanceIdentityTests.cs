using System;
using System.Collections.Generic;
using AlterumModelSwap;
using Xunit;

namespace AlterumModelSwap.InstanceLabeling.Tests;

public class InstanceIdentityTests
{
    private const string MainExe = @"C:\AlterumPinned\Alterum.exe";
    private const string BExe = @"C:\AlterumB\Alterum.exe";
    private const string MainLabel = "Alterum - MAIN (all features) [pinned]";
    private const string BLabel = "Alterum B - SAHUR TEST";
    private const int Pid = 4242;

    private static InstanceLabelIdentity.Result Resolve(string exe, int pid, params string[] args)
    {
        var argv = new string[args.Length + 1];
        argv[0] = exe;
        Array.Copy(args, 0, argv, 1, args.Length);
        return InstanceLabelIdentity.Resolve(exe, argv, pid);
    }

    private static InstanceLabelIdentity.Result Resolve(string exe, params string[] args)
        => Resolve(exe, Pid, args);

    // ---------------------------------------------------------------- supported installs

    [Fact]
    public void MainInstall_OmittedArgument_DerivesMainFromPath()
    {
        var r = Resolve(MainExe);

        Assert.True(r.Ok);
        Assert.Equal("main", r.InstallationTag);
        Assert.Equal(MainLabel, r.InstallationLabel);
        Assert.Equal("main", r.EffectiveTag);
        Assert.False(r.HasInstanceArg);
        Assert.Null(r.RawInstanceArg);
        Assert.Equal(MainLabel + " [instance=main; pid=4242]", r.ExpectedTitle);
    }

    [Fact]
    public void BInstall_OmittedArgument_DerivesBFromPath()
    {
        var r = Resolve(BExe);

        Assert.True(r.Ok);
        Assert.Equal("b", r.InstallationTag);
        Assert.Equal(BLabel, r.InstallationLabel);
        Assert.Equal("b", r.EffectiveTag);
        Assert.Equal(BLabel + " [instance=b; pid=4242]", r.ExpectedTitle);
    }

    [Fact]
    public void BInstall_InlineArgument_Accepted()
    {
        var r = Resolve(BExe, "-alterumInstance=b");

        Assert.True(r.Ok);
        Assert.True(r.HasInstanceArg);
        Assert.Equal("b", r.RawInstanceArg);
        Assert.Equal("b", r.EffectiveTag);
        Assert.Equal(BLabel + " [instance=b; pid=4242]", r.ExpectedTitle);
    }

    [Fact]
    public void BInstall_SeparateArgumentForm_Accepted()
    {
        var r = Resolve(BExe, "-alterumInstance", "b");

        Assert.True(r.Ok);
        Assert.Equal("b", r.RawInstanceArg);
        Assert.Equal("b", r.EffectiveTag);
    }

    [Theory]
    [InlineData("sahur-test")]
    [InlineData("qc")]
    [InlineData("matrix-26642252")]
    [InlineData("0123")]
    public void NonReservedTag_AllowedInMainInstall(string tag)
    {
        var r = Resolve(MainExe, "-alterumInstance=" + tag);

        Assert.True(r.Ok);
        Assert.Equal("main", r.InstallationTag);
        Assert.Equal(tag, r.EffectiveTag);
        Assert.Equal(MainLabel + " [instance=" + tag + "; pid=4242]", r.ExpectedTitle);
    }

    [Fact]
    public void NonReservedTag_AllowedInBInstall()
    {
        var r = Resolve(BExe, "-alterumInstance", "qc");

        Assert.True(r.Ok);
        Assert.Equal("b", r.InstallationTag);
        Assert.Equal("qc", r.EffectiveTag);
        Assert.Equal(BLabel + " [instance=qc; pid=4242]", r.ExpectedTitle);
    }

    [Theory]
    [InlineData(@"c:\alterumpinned\alterum.exe")]
    [InlineData("C:/AlterumPinned/Alterum.exe")]
    [InlineData(@"C:\AlterumPinned\.\Alterum.exe")]
    [InlineData(@"C:\AlterumPinned\sub\..\Alterum.exe")]
    [InlineData(@"\\?\C:\AlterumPinned\Alterum.exe")]
    [InlineData(@"C:\ALTERUMPINNED\ALTERUM.EXE")]
    public void SupportedPath_IsComparedCaseInsensitivelyAfterNormalization(string exe)
    {
        var r = Resolve(exe);

        Assert.True(r.Ok);
        Assert.Equal("main", r.InstallationTag);
        // Normalization preserves segment casing; only the comparison is case-insensitive.
        Assert.True(string.Equals(InstanceLabelIdentity.MainExePath, r.Exe, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeExePath_IsIdempotent()
    {
        string once = InstanceLabelIdentity.NormalizeExePath(@"c:/AlterumPinned//sub/../Alterum.exe");
        string twice = InstanceLabelIdentity.NormalizeExePath(once);

        Assert.Equal(@"C:\AlterumPinned\Alterum.exe", once);
        Assert.Equal(once, twice);
    }

    // ---------------------------------------------------------------- argument faults

    [Theory]
    [InlineData("-alterumInstance=two words")]
    [InlineData("-alterumInstance= b")]
    [InlineData("-alterumInstance=b ")]
    [InlineData("-alterumInstance=b!")]
    [InlineData("-alterumInstance=_b")]
    [InlineData("-alterumInstance=-b")]
    [InlineData("-alterumInstance=b.b")]
    [InlineData("-alterumInstance=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 chars
    public void MalformedValue_IsFatal(string arg)
    {
        var r = Resolve(BExe, arg);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ArgMalformed, r.Fault);
        Assert.Equal("arg-malformed", r.Code);
    }

    [Fact]
    public void SeparateFormValueWithWhitespace_IsFatal()
    {
        var r = Resolve(BExe, "-alterumInstance", " b");

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ArgMalformed, r.Fault);
    }

    [Fact]
    public void EmptyValue_IsFatal()
    {
        var inline = Resolve(BExe, "-alterumInstance=");
        var separate = Resolve(BExe, "-alterumInstance", "");

        Assert.Equal(InstanceLabelIdentity.Fault.ArgEmpty, inline.Fault);
        Assert.Equal("arg-empty", inline.Code);
        Assert.Equal(InstanceLabelIdentity.Fault.ArgEmpty, separate.Fault);
    }

    [Theory]
    [InlineData("-alterumInstance")]
    [InlineData("-alterumInstance", "-other")]
    [InlineData("-alterumInstance", "-alterumInstance=b")]
    public void MissingValue_IsFatal(params string[] args)
    {
        var r = Resolve(BExe, args);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ArgMissingValue, r.Fault);
        Assert.Equal("arg-missing-value", r.Code);
    }

    [Theory]
    [InlineData("-alterumInstance=b", "-alterumInstance=b")]
    [InlineData("-alterumInstance", "b", "-alterumInstance=b")]
    [InlineData("-alterumInstance=B", "-alterumInstance=b")]
    [InlineData("-alterumInstance=qc", "-alterumInstance=other")]
    public void DuplicateOccurrence_IsFatalEvenWhenValuesAreEqual(params string[] args)
    {
        var r = Resolve(BExe, args);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ArgDuplicate, r.Fault);
        Assert.Equal("arg-duplicate", r.Code);
    }

    [Fact]
    public void FlagName_IsCaseInsensitive()
    {
        var inline = Resolve(BExe, "-alteruminstance=b");
        var separate = Resolve(BExe, "-ALTERUMINSTANCE", "b");

        Assert.True(inline.Ok);
        Assert.Equal("b", inline.EffectiveTag);
        Assert.True(separate.Ok);
        Assert.Equal("b", separate.EffectiveTag);
    }

    [Fact]
    public void Value_IsLowercasedForIdentity()
    {
        var r = Resolve(BExe, "-alterumInstance=B");

        Assert.True(r.Ok);
        Assert.Equal("B", r.RawInstanceArg);
        Assert.Equal("b", r.EffectiveTag);
        Assert.Equal(BLabel + " [instance=b; pid=4242]", r.ExpectedTitle);
    }

    // ---------------------------------------------------------------- reserved tags

    [Theory]
    [InlineData(MainExe, "-alterumInstance=b")]
    [InlineData(MainExe, "-alterumInstance=B")]
    [InlineData(BExe, "-alterumInstance=main")]
    [InlineData(BExe, "-alterumInstance=MAIN")]
    public void ReservedTagInTheWrongInstall_IsFatal(string exe, string arg)
    {
        var r = Resolve(exe, arg);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ReservedTag, r.Fault);
        Assert.Equal("reserved-tag", r.Code);
    }

    [Fact]
    public void ReservedTagInItsOwnInstall_IsAllowed()
    {
        var main = Resolve(MainExe, "-alterumInstance=main");
        var b = Resolve(BExe, "-alterumInstance=b");

        Assert.True(main.Ok);
        Assert.Equal("main", main.EffectiveTag);
        Assert.True(b.Ok);
        Assert.Equal("b", b.EffectiveTag);
    }

    // ---------------------------------------------------------------- ordinary (portable) installs

    // A copy of the game that is not one of the two exact pinned paths is an ordinary install: it
    // runs, but under the distinct "portable" identity, so its window title and evidence can never
    // pass for the MAIN [pinned] or B install.
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe")]
    [InlineData(@"C:\Games\Alterum\Alterum.exe")]
    [InlineData(@"D:\Games\Alterum\Alterum.exe")]
    [InlineData(@"C:\AlterumDev\Alterum.exe")]
    [InlineData(@"C:\AlterumT1\Alterum.exe")]
    [InlineData(@"D:\AlterumPinned\Alterum.exe")] // same leaf, different drive: not this machine's pin
    [InlineData(@"\\server\share\Alterum\Alterum.exe")]
    public void OrdinaryInstall_OmittedArgument_DerivesPortableIdentity(string exe)
    {
        var r = Resolve(exe);

        Assert.True(r.Ok);
        Assert.Equal(InstanceLabelIdentity.PortableTag, r.InstallationTag);
        Assert.Equal(InstanceLabelIdentity.PortableLabel, r.InstallationLabel);
        Assert.Equal("portable", r.EffectiveTag);
        Assert.False(r.HasInstanceArg);
        Assert.Null(r.RawInstanceArg);
        Assert.Equal(exe, r.ExeRaw);
        Assert.Equal(InstanceLabelIdentity.NormalizeExePath(exe), r.Exe);
        Assert.Equal("Alterum (portable) [instance=portable; pid=4242]", r.ExpectedTitle);
    }

    [Theory]
    [InlineData(@"C:/Program Files (x86)/Steam/steamapps/common/Alterum/Alterum.exe")]
    [InlineData(@"\\?\D:\Games\Alterum\Alterum.exe")]
    [InlineData(@"D:\Games\.\Alterum\sub\..\Alterum.exe")]
    [InlineData(@"d:\games\ALTERUM\alterum.EXE")]
    public void OrdinaryInstall_PathIsNormalizedBeforeTheIdentityIsDerived(string exe)
    {
        var r = Resolve(exe);

        Assert.True(r.Ok);
        Assert.Equal("portable", r.InstallationTag);
        Assert.Equal(@"Alterum (portable) [instance=portable; pid=4242]", r.ExpectedTitle);
    }

    [Fact]
    public void OrdinaryInstall_LabelCannotBeMistakenForALocalInstall()
    {
        var ordinary = Resolve(@"C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe");
        var main = Resolve(MainExe);
        var b = Resolve(BExe);

        Assert.NotEqual(main.InstallationLabel, ordinary.InstallationLabel);
        Assert.NotEqual(b.InstallationLabel, ordinary.InstallationLabel);
        Assert.NotEqual(main.InstallationTag, ordinary.InstallationTag);
        Assert.NotEqual(b.InstallationTag, ordinary.InstallationTag);
        Assert.NotEqual(main.ExpectedTitle, ordinary.ExpectedTitle);
        Assert.NotEqual(b.ExpectedTitle, ordinary.ExpectedTitle);
        // The label must never inherit the local pin's claim.
        Assert.DoesNotContain("pinned", ordinary.InstallationLabel);
    }

    [Theory]
    [InlineData("qc")]
    [InlineData("sahur-test")]
    [InlineData("0123")]
    public void OrdinaryInstall_NonReservedTag_IsAccepted(string tag)
    {
        var inline = Resolve(@"D:\Games\Alterum\Alterum.exe", "-alterumInstance=" + tag);
        var separate = Resolve(@"D:\Games\Alterum\Alterum.exe", "-alterumInstance", tag);

        Assert.True(inline.Ok);
        Assert.Equal("portable", inline.InstallationTag);
        Assert.Equal(tag, inline.EffectiveTag);
        Assert.Equal("Alterum (portable) [instance=" + tag + "; pid=4242]", inline.ExpectedTitle);
        Assert.True(separate.Ok);
        Assert.Equal("portable", separate.InstallationTag);
        Assert.Equal(tag, separate.EffectiveTag);
    }

    // The strict local protections survive on a portable install: it may not borrow either local
    // label, because that would put "MAIN [pinned]" (or the B label) on a machine that is not this
    // one. Conversely, "portable" was and stays an ordinary tag on the two local installs: the
    // local labels stay exact-path, so no new reservation is needed to keep them unambiguous.
    [Theory]
    [InlineData("-alterumInstance=main")]
    [InlineData("-alterumInstance=MAIN")]
    [InlineData("-alterumInstance=b")]
    [InlineData("-alterumInstance=B")]
    public void OrdinaryInstall_ReservedLocalTag_IsFatal(string arg)
    {
        var r = Resolve(@"C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe", arg);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.ReservedTag, r.Fault);
        Assert.Equal("reserved-tag", r.Code);
        Assert.Null(r.ExpectedTitle);
    }

    [Fact]
    public void OrdinaryInstall_ExplicitPortableTag_IsAccepted()
    {
        var r = Resolve(@"D:\Games\Alterum\Alterum.exe", "-alterumInstance=portable");

        Assert.True(r.Ok);
        Assert.Equal("portable", r.RawInstanceArg);
        Assert.Equal("portable", r.EffectiveTag);
        Assert.Equal("Alterum (portable) [instance=portable; pid=4242]", r.ExpectedTitle);
    }

    // No new reservation on the local installs: 'portable' keeps the meaning it had before this
    // change, and the pinned/B label in the title is what keeps it unambiguous.
    [Fact]
    public void LocalInstall_PortableTag_RemainsAnOrdinaryInstanceTag()
    {
        var main = Resolve(MainExe, "-alterumInstance=portable");
        var b = Resolve(BExe, "-alterumInstance=portable");

        Assert.True(main.Ok);
        Assert.Equal("main", main.InstallationTag);
        Assert.Equal(MainLabel + " [instance=portable; pid=4242]", main.ExpectedTitle);
        Assert.True(b.Ok);
        Assert.Equal("b", b.InstallationTag);
        Assert.Equal(BLabel + " [instance=portable; pid=4242]", b.ExpectedTitle);
    }

    [Fact]
    public void OrdinaryInstall_TwoProcesses_GetDistinctTitles()
    {
        var first = Resolve(@"D:\Games\Alterum\Alterum.exe", 900);
        var second = Resolve(@"D:\Games\Alterum\Alterum.exe", 901);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.ExpectedTitle, second.ExpectedTitle);
        Assert.EndsWith("[instance=portable; pid=900]", first.ExpectedTitle);
        Assert.EndsWith("[instance=portable; pid=901]", second.ExpectedTitle);
    }

    [Theory]
    [InlineData(@"C:\Alterum.exe")]
    [InlineData(@"D:\Games\Alterum\Alterum.exe")]
    [InlineData(@"\\server\share\Alterum.exe")]
    public void IsAbsoluteExePath_AcceptsFullyQualifiedPaths(string exe)
    {
        Assert.True(InstanceLabelIdentity.IsAbsoluteExePath(InstanceLabelIdentity.NormalizeExePath(exe)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alterum.exe")]
    [InlineData(@"Alterum\Alterum.exe")]
    [InlineData(@"C:Alterum.exe")]
    [InlineData(@"\Alterum.exe")]
    public void IsAbsoluteExePath_RejectsRelativePaths(string exe)
    {
        Assert.False(InstanceLabelIdentity.IsAbsoluteExePath(InstanceLabelIdentity.NormalizeExePath(exe)));
    }

    // ---------------------------------------------------------------- unrecognised executables

    // Portable is not a licence to label anything that happens to host the plugin: the guard's
    // evidence is only meaningful when it names an Alterum executable at an absolute path, so a
    // relative or non-Alterum path stays fatal.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alterum.exe")]
    [InlineData(@"Alterum\Alterum.exe")]
    [InlineData(@"C:Alterum.exe")]
    [InlineData(@"C:\AlterumPinned\Alterum.exe.bak")]
    [InlineData(@"C:\AlterumPinned\notthegame.exe")]
    [InlineData(@"C:\AlterumPinned")]
    [InlineData(@"C:\AlterumPinned\")]
    public void UnrecognisedExecutablePath_IsFatal(string exe)
    {
        var r = Resolve(exe);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.UnknownExe, r.Fault);
        Assert.Equal("unknown-exe", r.Code);
        Assert.False(r.HasInstanceArg);
        Assert.Null(r.ExpectedTitle);
        Assert.Null(r.InstallationTag);
        Assert.Null(r.InstallationLabel);
    }

    [Fact]
    public void UnrecognisedExecutablePath_IsFatalEvenWithAValidTag()
    {
        var r = Resolve(@"C:\AlterumPinned\notthegame.exe", "-alterumInstance=qc");

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.UnknownExe, r.Fault);
    }

    [Fact]
    public void UnrecognisedExecutablePath_DetailNamesTheReasonWithoutEchoingThePath()
    {
        var relative = Resolve("Alterum.exe");
        var wrongName = Resolve(@"C:\Games\Alterum\AlterumLauncher.exe");

        Assert.Equal("executable path is not absolute", relative.FaultDetail);
        Assert.Equal("executable file name is not Alterum.exe", wrongName.FaultDetail);
        Assert.DoesNotContain(@"C:\Games", wrongName.FaultDetail);
    }

    // ---------------------------------------------------------------- title / pid

    [Fact]
    public void TwoProcessesFromTheSamePath_YieldDifferentTitles()
    {
        var first = Resolve(MainExe, 1000);
        var second = Resolve(MainExe, 1001);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.ExpectedTitle, second.ExpectedTitle);
        Assert.EndsWith("[instance=main; pid=1000]", first.ExpectedTitle);
        Assert.EndsWith("[instance=main; pid=1001]", second.ExpectedTitle);
    }

    [Fact]
    public void TitleFormat_IsExactlyLabelTagPid()
    {
        var r = Resolve(BExe, 77, "-alterumInstance=sahur-test");

        Assert.Equal("Alterum B - SAHUR TEST [instance=sahur-test; pid=77]", r.ExpectedTitle);
        Assert.Equal(r.ExpectedTitle, InstanceLabelIdentity.BuildTitle(BLabel, "sahur-test", 77));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("a_b-c")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    public void TagBoundary_Valid(string tag)
    {
        var r = Resolve(MainExe, "-alterumInstance=" + tag);

        Assert.True(r.Ok);
    }

    [Fact]
    public void TagBoundary_OneCharTooLong_Invalid()
    {
        var tag = new string('a', 33);
        var r = Resolve(MainExe, "-alterumInstance=" + tag);

        Assert.Equal(InstanceLabelIdentity.Fault.ArgMalformed, r.Fault);
    }

    // ---------------------------------------------------------------- launch token

    [Fact]
    public void LaunchToken_IsRecordedWhenPresent()
    {
        var r = Resolve(MainExe, "-alterumLabelLaunch=9f1c6a2e-77b1-4b5e-9a1e-123456789abc");

        Assert.True(r.Ok);
        Assert.Equal("9f1c6a2e-77b1-4b5e-9a1e-123456789abc", r.LaunchToken);
    }

    [Fact]
    public void LaunchToken_AbsenceIsNotAFailure()
    {
        var r = Resolve(MainExe);

        Assert.True(r.Ok);
        Assert.Null(r.LaunchToken);
    }

    [Theory]
    [InlineData("-alterumLabelLaunch=")]
    [InlineData("-alterumLabelLaunch")]
    [InlineData("-alterumLabelLaunchWithNoEquals=abc")]
    public void LaunchToken_MalformedOrEmptyIsIgnored(string arg)
    {
        var r = Resolve(MainExe, arg);

        Assert.True(r.Ok);
        Assert.Null(r.LaunchToken);
    }

    [Fact]
    public void LaunchToken_DoesNotAffectIdentity()
    {
        var r = Resolve(BExe, "-alterumLabelLaunch=abc", "-alterumInstance=b");

        Assert.True(r.Ok);
        Assert.Equal("abc", r.LaunchToken);
        Assert.Equal("b", r.EffectiveTag);
        Assert.Equal(BLabel + " [instance=b; pid=4242]", r.ExpectedTitle);
    }
}
