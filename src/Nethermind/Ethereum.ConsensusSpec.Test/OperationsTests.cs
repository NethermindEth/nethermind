// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
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
        {
            Attestation.Decode(ssz, out Attestation value);
            BlockProcessing.ProcessAttestation(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["attester_slashing"] = ("attester_slashing.ssz_snappy", (ctx, ssz) =>
        {
            AttesterSlashing.Decode(ssz, out AttesterSlashing value);
            BlockProcessing.ProcessAttesterSlashing(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz) =>
        {
            BeaconBlock.Decode(ssz, out BeaconBlock value);
            BlockProcessing.ProcessBlockHeader(ctx.State, value);
        }
        ),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz) =>
        {
            SignedBlsToExecutionChange.Decode(ssz, out SignedBlsToExecutionChange value);
            BlockProcessing.ProcessBlsToExecutionChange(ctx.State, value, ctx.VerifySignatures);
        }
        ),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz) =>
        {
            ConsolidationRequest.Decode(ssz, out ConsolidationRequest value);
            BlockProcessing.ProcessConsolidationRequest(ctx.State, value, ctx.Cache);
        }
        ),
        ["deposit"] = ("deposit.ssz_snappy", (ctx, ssz) =>
        {
            Deposit.Decode(ssz, out Deposit value);
            BlockProcessing.ProcessDeposit(ctx.State, value);
        }
        ),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz) =>
        {
            DepositRequest.Decode(ssz, out DepositRequest value);
            BlockProcessing.ProcessDepositRequest(ctx.State, value);
        }
        ),
        ["execution_payload"] = ("body.ssz_snappy", (ctx, ssz) =>
        {
            BeaconBlockBody.Decode(ssz, out BeaconBlockBody value);
            FixedNewPayloadNotifier notifier = new(ctx.ExecutionValid);
            BlockProcessing.ProcessExecutionPayload(ctx.State, value, notifier, ctx.Spec.MaxBlobsPerBlockElectra);
        }
        ),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz) =>
        {
            ProposerSlashing.Decode(ssz, out ProposerSlashing value);
            BlockProcessing.ProcessProposerSlashing(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz) =>
        {
            SyncAggregate.Decode(ssz, out SyncAggregate value);
            BlockProcessing.ProcessSyncAggregate(ctx.State, value, ctx.Cache, ctx.VerifySignatures);
        }
        ),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", (ctx, ssz) =>
        {
            SignedVoluntaryExit.Decode(ssz, out SignedVoluntaryExit value);
            BlockProcessing.ProcessVoluntaryExit(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz) =>
        {
            WithdrawalRequest.Decode(ssz, out WithdrawalRequest value);
            BlockProcessing.ProcessWithdrawalRequest(ctx.State, value, ctx.Cache);
        }
        ),
        ["withdrawals"] = ("execution_payload.ssz_snappy", (ctx, ssz) =>
        {
            ExecutionPayload.Decode(ssz, out ExecutionPayload value);
            BlockProcessing.ProcessWithdrawals(ctx.State, value);
        }
        ),
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
        {
            AttesterSlashingGloas.Decode(ssz, out AttesterSlashingGloas value);
            GloasBlockProcessing.ProcessAttesterSlashing(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz) =>
        {
            BeaconBlockGloas.Decode(ssz, out BeaconBlockGloas value);
            GloasBlockProcessing.ProcessBlockHeader(ctx.State, value);
        }
        ),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz) =>
        {
            SignedBlsToExecutionChange.Decode(ssz, out SignedBlsToExecutionChange value);
            GloasBlockProcessing.ProcessBlsToExecutionChange(ctx.State, value, ctx.VerifySignatures);
        }
        ),
        ["builder_deposit_request"] = ("builder_deposit_request.ssz_snappy", (ctx, ssz) =>
        {
            BuilderDepositRequest.Decode(ssz, out BuilderDepositRequest value);
            GloasBlockProcessing.ProcessBuilderDepositRequest(ctx.State, value);
        }
        ),
        ["builder_exit_request"] = ("builder_exit_request.ssz_snappy", (ctx, ssz) =>
        {
            BuilderExitRequest.Decode(ssz, out BuilderExitRequest value);
            GloasBlockProcessing.ProcessBuilderExitRequest(ctx.State, value);
        }
        ),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz) =>
        {
            ConsolidationRequest.Decode(ssz, out ConsolidationRequest value);
            GloasBlockProcessing.ProcessConsolidationRequest(ctx.State, value, ctx.Cache);
        }
        ),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz) =>
        {
            DepositRequest.Decode(ssz, out DepositRequest value);
            GloasBlockProcessing.ProcessDepositRequest(ctx.State, value);
        }
        ),
        ["execution_payload_bid"] = ("execution_payload_bid.ssz_snappy", (ctx, ssz) =>
        {
            SignedExecutionPayloadBid.Decode(ssz, out SignedExecutionPayloadBid value);
            GloasBlockProcessing.ProcessExecutionPayloadBid(ctx.State, value, ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["parent_execution_payload"] = ("block.ssz_snappy", (ctx, ssz) =>
        {
            BeaconBlockGloas.Decode(ssz, out BeaconBlockGloas value);
            GloasBlockProcessing.ProcessParentExecutionPayload(ctx.State, value, ctx.Cache);
        }
        ),
        ["payload_attestation"] = ("payload_attestation.ssz_snappy", (ctx, ssz) =>
        {
            PayloadAttestation.Decode(ssz, out PayloadAttestation value);
            GloasBlockProcessing.ProcessPayloadAttestation(ctx.State, value, ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz) =>
        {
            ProposerSlashing.Decode(ssz, out ProposerSlashing value);
            GloasBlockProcessing.ProcessProposerSlashing(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures);
        }
        ),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz) =>
        {
            SyncAggregate.Decode(ssz, out SyncAggregate value);
            GloasBlockProcessing.ProcessSyncAggregate(ctx.State, value, ctx.Cache, ctx.VerifySignatures);
        }
        ),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        // The churn-boundary cases of process_voluntary_exit, generated under their own handler name.
        ["voluntary_exit_churn"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz) =>
        {
            WithdrawalRequest.Decode(ssz, out WithdrawalRequest value);
            GloasBlockProcessing.ProcessWithdrawalRequest(ctx.State, value, ctx.Cache);
        }
        ),
        ["withdrawals"] = (null, (ctx, _) => GloasBlockProcessing.ProcessWithdrawals(ctx.State)),
    };

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

    private static void Execute(OperationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("operations", testCase.Fork, testCase.Preset, testCase.VectorName, () =>
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
        });

    private static void Run<TState>(OperationCase testCase, ForkDriver<TState> driver, Dictionary<string, (string? File, Action<OpContext<TState>, byte[]> Apply)> handlers)
        where TState : class
    {
        if (!handlers.TryGetValue(testCase.OperationName, out (string? File, Action<OpContext<TState>, byte[]> Apply) handler))
            throw new NotImplementedInDriverException($"operation '{testCase.OperationName}' has no handler in this driver.");

        string? operandPath = handler.File is null ? null : Path.Combine(testCase.CasePath, handler.File);
        if (operandPath is not null && !File.Exists(operandPath))
            throw new NotImplementedInDriverException($"expected operand file '{handler.File}' is missing for this vector.");

        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        OpContext<TState> ctx = new(
            state,
            driver.NewCache(),
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

            FuluDriverSupport.AssertPostStateRoot(driver, postPath, ctx.State);
        }
        else
        {
            FuluDriverSupport.AssertRejected(thrown, "the operation");
        }
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;
        foreach (TestCaseData data in Cases(ConsensusPreset.Mainnet))
            yield return data;
    }

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset)
    {
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            string? operationsRoot = ConsensusSpecArchive.SuitePath(preset, fork, "operations");
            if (operationsRoot is null)
                continue;

            foreach (string opDir in Directory.GetDirectories(operationsRoot))
            {
                string opName = Path.GetFileName(opDir);
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(opDir, "pre.ssz_snappy"))
                {
                    string vectorName = $"{preset}/{fork}/operations/{opName}/{Path.GetFileName(caseDir)}";
                    OperationCase testCase = new(preset.ToString(), fork, opName, caseDir, vectorName);
                    yield return new TestCaseData(testCase).SetName(vectorName);
                }
            }
        }
    }
}

public readonly record struct OperationCase(string Preset, string Fork, string OperationName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
