// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>operations</c> suite against this repo's per-operation
/// <c>BlockProcessing.Process*</c> methods, which are documented as "independently callable, matching
/// the per-operation spec test fixtures" - this suite is exactly what that sentence describes - and,
/// for Gloas, the <c>GloasBlockProcessing.Process*</c> ones. Driven
/// for every fork in <see cref="ConsensusSpecArchive.StateTransitionForks"/> through its
/// <see cref="ForkDriver"/>; earlier forks have no state container in this repo and are not enumerated
/// (see <see cref="ConsensusSpecArchive"/>). Minimal-preset vectors are reported not-implemented,
/// named and counted, never silently skipped.
/// </summary>
[TestFixture]
public class OperationsTests
{
    private readonly record struct OpContext<TState>(TState State, EpochCache Cache, PubkeyCache Pubkeys, bool VerifySignatures, bool ExecutionValid, BeaconChainSpec Spec, string CasePath);

    /// <summary>operation folder name -> (operand file name, action applied to the decoded state).</summary>
    private static readonly Dictionary<string, (string? File, Action<OpContext<BeaconStateFulu>, byte[]> Apply)> Handlers = new(StringComparer.Ordinal)
    {
        ["attestation"] = ("attestation.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessAttestation(ctx.State, Decode<Attestation>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["attester_slashing"] = ("attester_slashing.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessAttesterSlashing(ctx.State, Decode<AttesterSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessBlockHeader(ctx.State, Decode<BeaconBlock>(ssz))),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessBlsToExecutionChange(ctx.State, Decode<SignedBlsToExecutionChange>(ssz), ctx.VerifySignatures)),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessConsolidationRequest(ctx.State, Decode<ConsolidationRequest>(ssz), ctx.Cache)),
        ["deposit"] = ("deposit.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessDeposit(ctx.State, Decode<Deposit>(ssz))),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessDepositRequest(ctx.State, Decode<DepositRequest>(ssz))),
        ["execution_payload"] = ("body.ssz_snappy", (ctx, ssz) =>
        {
            BeaconBlockBody.Decode(ssz, out BeaconBlockBody value);
            FixedNewPayloadNotifier notifier = new(ctx.ExecutionValid);
            BlockProcessing.ProcessExecutionPayload(ctx.State, value, notifier, ctx.Spec.MaxBlobsPerBlockElectra);
        }
        ),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessProposerSlashing(ctx.State, Decode<ProposerSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessSyncAggregate(ctx.State, Decode<SyncAggregate>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessVoluntaryExit(ctx.State, Decode<SignedVoluntaryExit>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessWithdrawalRequest(ctx.State, Decode<WithdrawalRequest>(ssz), ctx.Cache)),
        ["withdrawals"] = ("execution_payload.ssz_snappy", (ctx, ssz) =>
            BlockProcessing.ProcessWithdrawals(ctx.State, Decode<ExecutionPayload>(ssz))),
    };

    /// <summary>
    /// The Gloas handlers (tests/formats/operations/README.md): <c>execution_payload</c> is gone, <c>withdrawals</c>
    /// takes no operand, and <c>attestation</c> reads <c>parent_slot</c> from meta.yaml.
    /// </summary>
    private static readonly Dictionary<string, (string? File, Action<OpContext<BeaconStateGloas>, byte[]> Apply)> GloasHandlers = new(StringComparer.Ordinal)
    {
        ["attestation"] = ("attestation.ssz_snappy", (ctx, ssz) =>
        {
            AttestationGloas.Decode(ssz, out AttestationGloas value);
            ulong parentSlot = ulong.Parse(FuluDriverSupport.ParseFlowMap(Path.Combine(ctx.CasePath, "meta.yaml"))["parent_slot"]);
            GloasBlockProcessing.ProcessAttestation(ctx.State, value, parentSlot, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["attester_slashing"] = ("attester_slashing.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessAttesterSlashing(ctx.State, Decode<AttesterSlashingGloas>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessBlockHeader(ctx.State, Decode<BeaconBlockGloas>(ssz))),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessBlsToExecutionChange(ctx.State, Decode<SignedBlsToExecutionChange>(ssz), ctx.VerifySignatures)),
        ["builder_deposit_request"] = ("builder_deposit_request.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessBuilderDepositRequest(ctx.State, Decode<BuilderDepositRequest>(ssz))),
        ["builder_exit_request"] = ("builder_exit_request.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessBuilderExitRequest(ctx.State, Decode<BuilderExitRequest>(ssz))),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessConsolidationRequest(ctx.State, Decode<ConsolidationRequest>(ssz), ctx.Cache)),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessDepositRequest(ctx.State, Decode<DepositRequest>(ssz))),
        ["execution_payload_bid"] = ("execution_payload_bid.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(ctx.State, Decode<SignedExecutionPayloadBid>(ssz), ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures)),
        ["parent_execution_payload"] = ("block.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessParentExecutionPayload(ctx.State, Decode<BeaconBlockGloas>(ssz), ctx.Cache)),
        ["payload_attestation"] = ("payload_attestation.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessPayloadAttestation(ctx.State, Decode<PayloadAttestation>(ssz), ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures)),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessProposerSlashing(ctx.State, Decode<ProposerSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessSyncAggregate(ctx.State, Decode<SyncAggregate>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures)),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        // The churn-boundary cases of process_voluntary_exit, generated under their own handler name.
        ["voluntary_exit_churn"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz) =>
            GloasBlockProcessing.ProcessWithdrawalRequest(ctx.State, Decode<WithdrawalRequest>(ssz), ctx.Cache)),
        ["withdrawals"] = (null, (ctx, _) => GloasBlockProcessing.ProcessWithdrawals(ctx.State)),
    };

    private static T Decode<T>(byte[] ssz) where T : ISszCodec<T>
    {
        T.Decode(ssz, out T value);
        return value;
    }

    private static void ApplyGloasVoluntaryExit(OpContext<BeaconStateGloas> ctx, byte[] ssz)
    {
        SignedVoluntaryExit.Decode(ssz, out SignedVoluntaryExit value);
        GloasBlockProcessing.ProcessVoluntaryExit(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
    }

    private sealed class FixedNewPayloadNotifier(bool valid) : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => valid ? ExecutionStatus.Valid : ExecutionStatus.Invalid;
    }

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(OperationCase testCase) => Execute(testCase);

    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(OperationCase testCase) => Execute(testCase);

    /// <summary>Handlers of a fork's table that the fork has no vectors for at <see cref="ConsensusSpecArchive.Version"/>.</summary>
    private static readonly Dictionary<string, string[]> OperationsAbsentByFork = new(StringComparer.Ordinal)
    {
        ["fulu"] = ["deposit"],
    };

    // A wrong suite path or an emptied case source enumerates zero vectors; a renamed or dropped handler leaves its vectors not-implemented; both run green.
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

    // Not-implemented vectors are Inconclusive, so a handler that reports every mainnet vector that way still runs green.
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

    private static void Run<TState>(OperationCase testCase, ForkDriver<TState> driver, Dictionary<string, (string? File, Action<OpContext<TState>, byte[]> Apply)> handlers)
        where TState : class
    {
        if (!handlers.TryGetValue(testCase.OperationName, out (string? File, Action<OpContext<TState>, byte[]> Apply) handler))
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
        bool expectSuccess = File.Exists(postPath);

        Exception? thrown = null;
        try { handler.Apply(ctx, operand); }
        catch (Exception ex) { thrown = ex; }

        if (expectSuccess)
        {
            if (thrown is not null)
                Assert.Fail($"expected the operation to be accepted, but it threw: {thrown}");

            FuluDriverSupport.AssertPostStateRoot(driver, postPath, ctx.State, cache);
        }
        else
        {
            FuluDriverSupport.AssertRejected(thrown, "the operation");
        }
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
