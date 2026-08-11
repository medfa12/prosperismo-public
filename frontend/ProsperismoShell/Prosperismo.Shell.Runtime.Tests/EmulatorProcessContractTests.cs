// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class EmulatorProcessContractTests
{
    [Fact]
    public void NativeEmulatorNeverReceivesLegacyMitigatedChildProtocol()
    {
        var plan = EmulatorProcess.BuildWindowsStartPlan(
            @"C:\Prosperismo\prosperismo_emulator.exe",
            ["--game", @"D:\Games\Astro's Playroom\eboot.bin"],
            new Dictionary<string, string> { ["PROSPERISMO_LOG"] = "debug" },
            [new KeyValuePair<string, string>("PATH", @"C:\Windows")]);

        Assert.False(plan.UsesMitigatedChildProtocol);
        Assert.Equal(["--game", @"D:\Games\Astro's Playroom\eboot.bin"], plan.Arguments);
        Assert.False(plan.Environment.ContainsKey("PROSPERISMO_MITIGATED_CHILD"));
        Assert.Equal("debug", plan.Environment["PROSPERISMO_LOG"]);
    }

    [Fact]
    public void ExplicitLegacyHostKeepsMitigatedChildProtocol()
    {
        var plan = EmulatorProcess.BuildWindowsStartPlan(
            @"C:\Prosperismo\Prosperismo.exe",
            ["--game", @"D:\Games\eboot.bin"],
            suppliedEnvironment: null,
            inheritedEnvironment: []);

        Assert.True(plan.UsesMitigatedChildProtocol);
        Assert.Equal(
            ["--prosperismo-mitigated-child", "--game", @"D:\Games\eboot.bin"],
            plan.Arguments);
        Assert.Equal("1", plan.Environment["PROSPERISMO_MITIGATED_CHILD"]);
    }

    [Fact]
    public void UnknownTargetsDoNotReceiveMitigatedChildProtocol()
    {
        var plan = EmulatorProcess.BuildWindowsStartPlan(
            @"C:\Tools\another-emulator.exe",
            [],
            suppliedEnvironment: null,
            inheritedEnvironment: []);

        Assert.False(plan.UsesMitigatedChildProtocol);
        Assert.Empty(plan.Arguments);
        Assert.Empty(plan.Environment);
    }

    [Fact]
    public void WindowsPlanMergesSuppliedEnvironmentWithoutMutatingInheritedValues()
    {
        var plan = EmulatorProcess.BuildWindowsStartPlan(
            @"C:\Prosperismo\prosperismo_emulator.exe",
            [],
            new Dictionary<string, string>
            {
                ["PATH"] = @"D:\Prosperismo\runtime",
                ["VK_ICD_FILENAMES"] = @"D:\Prosperismo\MoltenVK_icd.json",
            },
            [
                new KeyValuePair<string, string>("PATH", @"C:\Windows"),
                new KeyValuePair<string, string>("UNCHANGED", "value"),
            ]);

        Assert.Equal(@"D:\Prosperismo\runtime", plan.Environment["Path"]);
        Assert.Equal(@"D:\Prosperismo\MoltenVK_icd.json", plan.Environment["VK_ICD_FILENAMES"]);
        Assert.Equal("value", plan.Environment["UNCHANGED"]);
    }

    [Fact]
    public void CommandLineUsesWindowsSafeQuotingForTheConstructedArguments()
    {
        var plan = EmulatorProcess.BuildWindowsStartPlan(
            @"C:\Program Files\Prosperismo\prosperismo_emulator.exe",
            ["--game", "C:\\Games\\A \"quoted\" game\\eboot.bin"],
            suppliedEnvironment: null,
            inheritedEnvironment: []);

        Assert.Equal(
            "\"C:\\Program Files\\Prosperismo\\prosperismo_emulator.exe\" --game \"C:\\Games\\A \\\"quoted\\\" game\\eboot.bin\"",
            EmulatorProcess.BuildCommandLine(@"C:\Program Files\Prosperismo\prosperismo_emulator.exe", plan.Arguments));
    }
}
