// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Runs the consensus-specs <c>operations</c> suite against this repo's per-operation
/// <c>BlockProcessing.Process*</c> methods, which are documented as "independently callable, matching
/// the per-operation spec test fixtures" - this suite is exactly what that sentence describes. Driven
/// for every fork in <see cref="ConsensusSpecArchive.StateTransitionForks"/> through its
/// <see cref="ForkDriver"/>; earlier forks have no state container in this repo and are not enumerated
/// (see <see cref="ConsensusSpecArchive"/>). Minimal-preset vectors are reported not-implemented,
/// named and counted, never silently skipped.
/// </summary>
[TestFixture]
public class OperationsTests
{
    private readonly record struct OpContext(BeaconStateFulu State, EpochCache Cache, PubkeyCache Pubkeys, bool VerifySignatures, bool ExecutionValid);

    /// <summary>operation folder name -> (operand file name, action applied to the decoded state).</summary>
    private static readonly Dictionary<string, (string File, Action<OpContext, byte[]> Apply)> Handlers = new(StringComparer.Ordinal)
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
            BlockProcessing.ProcessExecutionPayload(ctx.State, value, notifier, FuluDriverSupport.MaxBlobsPerBlockElectra);
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

            ForkDriver driver = FuluDriverSupport.RequireForkDriver(testCase.Fork);

            if (!Handlers.TryGetValue(testCase.OperationName, out (string File, Action<OpContext, byte[]> Apply) handler))
                throw new NotImplementedInDriverException($"operation '{testCase.OperationName}' has no handler in this driver.");

            string operandPath = Path.Combine(testCase.CasePath, handler.File);
            if (!File.Exists(operandPath))
                throw new NotImplementedInDriverException($"expected operand file '{handler.File}' is missing for this vector.");

            BeaconStateFulu state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
            OpContext ctx = new(
                state,
                driver.NewCache(),
                FuluDriverSupport.BuildPubkeyCache(state),
                FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath),
                FuluDriverSupport.ReadExecutionValid(testCase.CasePath));

            byte[] operand = SszConsensusTestLoader.ReadSszSnappy(operandPath);
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
            else if (thrown is null)
            {
                Assert.Fail("expected the operation to be rejected as invalid, but it completed without error");
            }
        });

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
