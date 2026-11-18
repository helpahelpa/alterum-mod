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

    // ---------------------------------------------------------------- unknown executables

    [Theory]
    [InlineData(@"C:\AlterumDev\Alterum.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Alterum\Alterum.exe")]
    [InlineData(@"C:\AlterumTests\123\Alterum.exe")]
    [InlineData(@"D:\AlterumPinned\Alterum.exe")]
    [InlineData(@"C:\AlterumPinned\Alterum.exe.bak")]
    [InlineData("Alterum.exe")]
    [InlineData("")]
    public void UnsupportedExecutablePath_IsFatal(string exe)
    {
        var r = Resolve(exe);

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.UnknownExe, r.Fault);
        Assert.Equal("unknown-exe", r.Code);
        Assert.False(r.HasInstanceArg);
    }

    [Fact]
    public void UnknownExecutablePath_IsFatalEvenWithAValidTag()
    {
        var r = Resolve(@"C:\AlterumDev\Alterum.exe", "-alterumInstance=qc");

        Assert.False(r.Ok);
        Assert.Equal(InstanceLabelIdentity.Fault.UnknownExe, r.Fault);
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
