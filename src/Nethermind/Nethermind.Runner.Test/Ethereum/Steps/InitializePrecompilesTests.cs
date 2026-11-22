// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[Parallelizable(ParallelScope.All)]
public class InitializePrecompilesTests
{
    // The outputs that the test vectors of the default probes give.
    private static readonly Dictionary<string, string> ExpectedProbeOutputs = new()
    {
        ["secp256k1"] = "000000000000000000000000ceaccac640adf55b2028469bd36ba501f28b699d",
        ["GMP"] = "04",
        ["MCL"] = "0a6678fd675aa4d8f0d03a1feb921a27f38ebdcb860cc083653519655acd6d79172fd5b3b2bfdd44e43bcec3eace9347608f9f0a16f1e184cb3f52e6f259cbeb",
        ["blst"] = "000000000000000000000000000000000a40300ce2dec9888b60690e9a41d3004fda4886854573974fab73b046d3147ba5b7a5bde85279ffede1b45b3918d82d" +
            "0000000000000000000000000000000006d3d887e9f53b9ec4eb6cedf5607226754b07c01ace7834f57f3e7315faefb739e59018e22c492006190fba4a870025",
        ["secp256r1"] = "0000000000000000000000000000000000000000000000000000000000000001",
    };

    private static IEnumerable<string> DefaultProbeLibraries => InitializePrecompiles.DefaultNativeLibraryProbes.Select(static probe => probe.Library);

    [Test]
    public void Default_probe_is_a_valid_call_needed_on_osaka([ValueSource(nameof(DefaultProbeLibraries))] string library)
    {
        InitializePrecompiles.NativeLibraryProbe probe = GetDefaultProbe(library);

        Result<byte[]> result = probe.Precompile.Run(probe.Input, Osaka.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Data, Is.EqualTo(Convert.FromHexString(ExpectedProbeOutputs[library])));
            Assert.That(probe.IsNeeded(Osaka.Instance), Is.True);
        }
    }

    [Test]
    public void Default_table_has_one_probe_per_library() =>
        Assert.That(DefaultProbeLibraries, Is.EquivalentTo(ExpectedProbeOutputs.Keys));

    // For each library, the last fork that does not need it and the first that does; blst also with BLS12-381 alone.
    private static IEnumerable<TestCaseData> ProbeGating()
    {
        static TestCaseData Case(string library, IReleaseSpec spec, bool isNeeded) =>
            new TestCaseData(library, spec, isNeeded).SetArgDisplayNames(library, spec.Name, isNeeded.ToString());

        yield return Case("secp256k1", Frontier.Instance, true);
        yield return Case("GMP", SpuriousDragon.Instance, false);
        yield return Case("GMP", Byzantium.Instance, true);
        yield return Case("MCL", SpuriousDragon.Instance, false);
        yield return Case("MCL", Byzantium.Instance, true);
        yield return Case("blst", Shanghai.Instance, false);
        yield return Case("blst", Cancun.Instance, true);
        yield return Case("blst", new ReleaseSpec { Name = "BLS12-381 without point evaluation", IsEip2537Enabled = true }, true);
        yield return Case("secp256r1", Prague.Instance, false);
        yield return Case("secp256r1", Osaka.Instance, true);
    }

    [TestCaseSource(nameof(ProbeGating))]
    public void Probe_is_needed_when_the_spec_enables_any_of_its_precompiles(string library, IReleaseSpec spec, bool isNeeded) =>
        Assert.That(GetDefaultProbe(library).IsNeeded(spec), Is.EqualTo(isNeeded));

    private static IEnumerable<TestCaseData> NativeLoadFailures()
    {
        yield return new TestCaseData(new DllNotFoundException("library not found")).SetArgDisplayNames("DllNotFound");
        yield return new TestCaseData(new TypeInitializationException("Nethermind.Evm.Precompiles.BN254", new DllNotFoundException("library not found")))
            .SetArgDisplayNames("TypeInitialization(DllNotFound)");
        yield return new TestCaseData(new BadImageFormatException("library for another platform")).SetArgDisplayNames("BadImageFormat");
        yield return new TestCaseData(new TypeInitializationException("Binding", new EntryPointNotFoundException("export not found")))
            .SetArgDisplayNames("TypeInitialization(EntryPointNotFound)");
    }

    [TestCaseSource(nameof(NativeLoadFailures))]
    public async Task Native_load_failure_stops_start_up_naming_each_needed_library(Exception fault)
    {
        (InitializePrecompiles step, TestErrorLogManager logManager, ProcessExitSource exitSource, FakePrecompile healthy) = CreateStepWithFaultyProbes(fault);

        await Assert.ThatAsync(() => step.Execute(CancellationToken.None), Throws.InstanceOf<OperationCanceledException>());

        string reason = (fault.InnerException ?? fault).Message;
        IEnumerable<string> errors = logManager.Errors.Select(static error => error.Text);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(ExitCodes.MissingPrecompile));
            Assert.That(exitSource.Token.IsCancellationRequested, Is.True);
            Assert.That(logManager.Errors, Has.Count.EqualTo(2));
            Assert.That(errors.Count(text => text.Contains("FirstLibrary") && text.Contains(reason)), Is.EqualTo(1));
            Assert.That(errors.Count(text => text.Contains("SecondLibrary") && text.Contains(reason)), Is.EqualTo(1));
            Assert.That(errors, Has.None.Contains("UnusedLibrary"));
            Assert.That(logManager.Errors.Select(static error => error.Exception), Is.All.Null);
            Assert.That(healthy.CallCount, Is.EqualTo(1), "a healthy probe after the failures");
            Assert.That(healthy.LastInput, Is.EqualTo(HealthyProbeInput));
            Assert.That(healthy.LastSpec, Is.SameAs(Berlin.Instance), "the final spec, not the genesis spec");
        }
    }

    private static IEnumerable<TestCaseData> OtherProbeFaults()
    {
        yield return new TestCaseData(new InvalidOperationException("not a load failure")).SetArgDisplayNames("InvalidOperation");
        yield return new TestCaseData(new TypeInitializationException("Binding", new InvalidOperationException("not a load failure")))
            .SetArgDisplayNames("TypeInitialization(InvalidOperation)");
    }

    [TestCaseSource(nameof(OtherProbeFaults))]
    public async Task Other_probe_fault_propagates_without_exit(Exception fault)
    {
        (InitializePrecompiles step, TestErrorLogManager logManager, ProcessExitSource exitSource, _) = CreateStepWithFaultyProbes(fault);

        await Assert.ThatAsync(() => step.Execute(CancellationToken.None), Throws.Exception.SameAs(fault));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(ExitCodes.Ok));
            Assert.That(exitSource.Token.IsCancellationRequested, Is.False);
            Assert.That(logManager.Errors, Is.Empty);
        }
    }

    [Test]
    public async Task Healthy_installation_starts()
    {
        TestErrorLogManager logManager = new();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        InitializePrecompiles step = new(MainnetSpecProvider.Instance, new InitConfig(), logManager, exitSource);

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            exitSource.DidNotReceiveWithAnyArgs().Exit(default);
            Assert.That(logManager.Errors, Is.Empty);
            Assert.That(step.NativeLibraryProbes, Is.SameAs(InitializePrecompiles.DefaultNativeLibraryProbes));
        }
    }

    private static InitializePrecompiles.NativeLibraryProbe GetDefaultProbe(string library) =>
        InitializePrecompiles.DefaultNativeLibraryProbes.Single(probe => probe.Library == library);

    private static readonly byte[] HealthyProbeInput = [1, 2, 3];

    /// <summary>
    /// A step whose final spec is Berlin, so without the KZG setup, and whose genesis spec is Frontier. Three probes throw
    /// <paramref name="fault"/>: two at precompiles that Berlin enables and one at a precompile that it does not.
    /// A healthy probe at a precompile that Berlin enables, and Frontier does not, comes last.
    /// </summary>
    private static (InitializePrecompiles Step, TestErrorLogManager LogManager, ProcessExitSource ExitSource, FakePrecompile Healthy) CreateStepWithFaultyProbes(Exception fault)
    {
        TestErrorLogManager logManager = new();
        ProcessExitSource exitSource = new(CancellationToken.None);
        FakePrecompile faulty = new(fault);
        FakePrecompile healthy = new();
        InitializePrecompiles step = new(new TestSpecProvider(Frontier.Instance) { NextForkSpec = Berlin.Instance }, new InitConfig(), logManager, exitSource)
        {
            NativeLibraryProbes =
            [
                new("FirstLibrary", "the first precompile", faulty, [], ECRecoverPrecompile.Address),
                new("SecondLibrary", "the second precompile", faulty, [], BN254AddPrecompile.Address),
                new("UnusedLibrary", "a precompile after Berlin", faulty, [], SecP256r1Precompile.Address),
                new("HealthyLibrary", "a healthy precompile", healthy, HealthyProbeInput, ModExpPrecompile.Address),
            ]
        };

        return (step, logManager, exitSource, healthy);
    }

    /// <summary>Records its last call and throws <paramref name="fault"/>, if one is given.</summary>
    private sealed class FakePrecompile(Exception? fault = null) : IPrecompile
    {
        public int CallCount { get; private set; }
        public byte[]? LastInput { get; private set; }
        public IReleaseSpec? LastSpec { get; private set; }

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            CallCount++;
            LastInput = inputData.ToArray();
            LastSpec = releaseSpec;
            return fault is null ? Array.Empty<byte>() : throw fault;
        }
    }
}
