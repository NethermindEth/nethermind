// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;
using static Ethereum.ConsensusSpec.Test.OperationVectorHandlers;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class OperationsTests
{
    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(OperationCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(OperationCase testCase) => Execute(testCase);

    [TestCase("fulu", "bls_to_execution_change", "success")]
    [TestCase("gloas", "bls_to_execution_change", "success")]
    [TestCase("fulu", "voluntary_exit", "basic")]
    [TestCase("gloas", "voluntary_exit", "basic")]
    [TestCase("fulu", "proposer_slashing", "basic")]
    [TestCase("gloas", "proposer_slashing", "basic")]
    [TestCase("gloas", "withdrawals", "early_return_empty_parent_block")]
    public void Required_successful_operation_contract(string fork, string operation, string name)
    {
        string path = Path.Combine(ConsensusSpecArchive.GetRoot(ConsensusPreset.Mainnet),
            "tests", "mainnet", fork, "operations", operation, "pyspec_tests", name);
        foreach (string file in new[] { "pre.ssz_snappy", "post.ssz_snappy" })
            Assert.That(File.Exists(Path.Combine(path, file)), Is.True, $"mandatory positive vector is missing {file}");
        Assert.That(() => Execute(new OperationCase(nameof(ConsensusPreset.Mainnet), fork, operation, path,
            $"mainnet/{fork}/operations/{operation}/pyspec_tests/{name}")), Throws.Nothing);
    }

    private static readonly Dictionary<string, string[]> OperationsAbsentByFork = new(StringComparer.Ordinal)
    {
        ["fulu"] = ["deposit"],
    };
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<OperationCase> cases = FuluDriverSupport.TestedCases<OperationCase>(preset, MinimalCases, MainnetCases);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks));
            foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
            {
                IEnumerable<string> table = FuluDriverSupport.RequireForkDriver(fork) is ForkDriver<BeaconStateGloas> ? GloasHandlers.Keys : Handlers.Keys;
                Assert.That(
                    cases.Where(testCase => testCase.Fork == fork).Select(static testCase => testCase.OperationName).Distinct(),
                    Is.EquivalentTo(table.Except(OperationsAbsentByFork.GetValueOrDefault(fork, []))),
                    $"{fork} operations");
            }
        }
    }
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<OperationCase>(ConsensusPreset.Mainnet, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{testCase.OperationName}",
            Run);

    private static void Execute(OperationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("operations", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(OperationCase testCase)
    {
        if (testCase.Preset == nameof(ConsensusPreset.Minimal))
        {
            throw new NotImplementedInDriverException(
                "This repo's BeaconState containers hard-code mainnet-preset-scaled vector bounds (see SszStaticTests' " +
                "BeaconState/Attestation/SyncCommittee entries), so they cannot decode a minimal-preset pre.ssz_snappy " +
                "at all; this suite only runs for real against the mainnet preset (opt in with " +
                "NETHERMIND_CONSENSUS_SPEC_MAINNET=1).");
        }

        switch (FuluDriverSupport.RequireForkDriver(testCase.Fork))
        {
            case ForkDriver<BeaconStateFulu> fulu:
                Run(testCase, fulu, Handlers);
                break;
            case ForkDriver<BeaconStateGloas> gloas:
                Run(testCase, gloas, GloasHandlers);
                break;
            case ForkDriver other:
                throw new NotImplementedInDriverException($"fork '{other.Fork}' has no operations handler table.");
        }
    }

    private static void Run<TState>(OperationCase testCase, ForkDriver<TState> driver, Dictionary<string, (string? File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply)> handlers)
        where TState : class
    {
        if (!handlers.TryGetValue(testCase.OperationName, out (string? File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply) handler))
            throw new NotImplementedInDriverException($"operation '{testCase.OperationName}' has no handler in this driver.");

        string? operandPath = handler.File is null ? null : Path.Combine(testCase.CasePath, handler.File);
        if (operandPath is not null && !File.Exists(operandPath))
            throw new NotImplementedInDriverException($"expected operand file '{handler.File}' is missing for this vector.");

        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        // Primes the incremental hasher so the post-state root is taken as a diff against the pre-state, as in production.
        driver.CachedRoot(state, cache);
        OpContext<TState> ctx = new(
            state,
            cache,
            FuluDriverSupport.BuildPubkeyCache(driver.ValidatorsOf(state)),
            FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath),
            FuluDriverSupport.ReadExecutionValid(testCase.CasePath),
            FuluDriverSupport.CaseSpec(testCase.CasePath),
            testCase.CasePath);

        byte[] operand = operandPath is null ? [] : SszConsensusTestLoader.ReadSszSnappy(operandPath);
        string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
        FuluDriverSupport.AssertTransition(driver, postPath, ctx.State, cache,
            () => handler.Apply(ctx, operand, null), "the operation",
            "expected the operation to be accepted, but it threw");
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, "operations", "pre.ssz_snappy",
            static (p, fork, handler, path, name) => new OperationCase(p.ToString(), fork, handler, path, name), strictHandlerDirectory: true);
}

public readonly record struct OperationCase(string Preset, string Fork, string OperationName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
