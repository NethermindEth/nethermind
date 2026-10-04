// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Ethereum.ConsensusSpec.Test;

internal static class OperationVectorHandlers
{
    internal readonly record struct OpContext<TState>(TState State, EpochCache Cache, PubkeyCache Pubkeys, bool VerifySignatures, bool ExecutionValid, BeaconChainSpec Spec, string CasePath);

    /// <summary>operation folder name -> (operand file name, action applied to the decoded state).</summary>
    internal static readonly Dictionary<string, (string? File, Action<OpContext<BeaconStateFulu>, byte[], BlockSignatureBatch?> Apply)> Handlers = new(StringComparer.Ordinal)
    {
        ["attestation"] = ("attestation.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessAttestation(ctx.State, Decode<Attestation>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["attester_slashing"] = ("attester_slashing.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessAttesterSlashing(ctx.State, Decode<AttesterSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessBlockHeader(ctx.State, Decode<BeaconBlock>(ssz))),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessBlsToExecutionChange(ctx.State, Decode<SignedBlsToExecutionChange>(ssz), ctx.VerifySignatures, batch)),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessConsolidationRequest(ctx.State, Decode<ConsolidationRequest>(ssz), ctx.Cache)),
        ["deposit"] = ("deposit.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessDeposit(ctx.State, Decode<Deposit>(ssz))),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessDepositRequest(ctx.State, Decode<DepositRequest>(ssz))),
        ["execution_payload"] = ("body.ssz_snappy", (ctx, ssz, batch) =>
        {
            BeaconBlockBody.Decode(ssz, out BeaconBlockBody value);
            FixedNewPayloadNotifier notifier = new(ctx.ExecutionValid);
            BlockProcessing.ProcessExecutionPayload(ctx.State, value, notifier, ctx.Spec.MaxBlobsPerBlockElectra);
        }
        ),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessProposerSlashing(ctx.State, Decode<ProposerSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessSyncAggregate(ctx.State, Decode<SyncAggregate>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessVoluntaryExit(ctx.State, Decode<SignedVoluntaryExit>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessWithdrawalRequest(ctx.State, Decode<WithdrawalRequest>(ssz), ctx.Cache)),
        ["withdrawals"] = ("execution_payload.ssz_snappy", (ctx, ssz, batch) =>
            BlockProcessing.ProcessWithdrawals(ctx.State, Decode<ExecutionPayload>(ssz))),
    };

    /// <summary>
    /// The Gloas handlers (tests/formats/operations/README.md): <c>execution_payload</c> is gone, <c>withdrawals</c>
    /// takes no operand, and <c>attestation</c> reads <c>parent_slot</c> from meta.yaml.
    /// </summary>
    internal static readonly Dictionary<string, (string? File, Action<OpContext<BeaconStateGloas>, byte[], BlockSignatureBatch?> Apply)> GloasHandlers = new(StringComparer.Ordinal)
    {
        ["attestation"] = ("attestation.ssz_snappy", (ctx, ssz, batch) =>
        {
            AttestationGloas.Decode(ssz, out AttestationGloas value);
            ulong parentSlot = ulong.Parse(FuluDriverSupport.ParseFlowMap(Path.Combine(ctx.CasePath, "meta.yaml"))["parent_slot"]);
            GloasBlockProcessing.ProcessAttestation(ctx.State, value, parentSlot, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch);
        }
        ),
        ["attester_slashing"] = ("attester_slashing.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessAttesterSlashing(ctx.State, Decode<AttesterSlashingGloas>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["block_header"] = ("block.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessBlockHeader(ctx.State, Decode<BeaconBlockGloas>(ssz))),
        ["bls_to_execution_change"] = ("address_change.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessBlsToExecutionChange(ctx.State, Decode<SignedBlsToExecutionChange>(ssz), ctx.VerifySignatures, batch)),
        ["builder_deposit_request"] = ("builder_deposit_request.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessBuilderDepositRequest(ctx.State, Decode<BuilderDepositRequest>(ssz))),
        ["builder_exit_request"] = ("builder_exit_request.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessBuilderExitRequest(ctx.State, Decode<BuilderExitRequest>(ssz))),
        ["consolidation_request"] = ("consolidation_request.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessConsolidationRequest(ctx.State, Decode<ConsolidationRequest>(ssz), ctx.Cache)),
        ["deposit_request"] = ("deposit_request.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessDepositRequest(ctx.State, Decode<DepositRequest>(ssz))),
        ["execution_payload_bid"] = ("execution_payload_bid.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(ctx.State, Decode<SignedExecutionPayloadBid>(ssz), ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["parent_execution_payload"] = ("block.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessParentExecutionPayload(ctx.State, Decode<BeaconBlockGloas>(ssz), ctx.Cache)),
        ["payload_attestation"] = ("payload_attestation.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessPayloadAttestation(ctx.State, Decode<PayloadAttestation>(ssz), ctx.Spec, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["proposer_slashing"] = ("proposer_slashing.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessProposerSlashing(ctx.State, Decode<ProposerSlashing>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["sync_aggregate"] = ("sync_aggregate.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessSyncAggregate(ctx.State, Decode<SyncAggregate>(ssz), ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch)),
        ["voluntary_exit"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        // The churn-boundary cases of process_voluntary_exit, generated under their own handler name.
        ["voluntary_exit_churn"] = ("voluntary_exit.ssz_snappy", ApplyGloasVoluntaryExit),
        ["withdrawal_request"] = ("withdrawal_request.ssz_snappy", (ctx, ssz, batch) =>
            GloasBlockProcessing.ProcessWithdrawalRequest(ctx.State, Decode<WithdrawalRequest>(ssz), ctx.Cache)),
        ["withdrawals"] = (null, (ctx, _, _) => GloasBlockProcessing.ProcessWithdrawals(ctx.State)),
    };

    private static T Decode<T>(byte[] ssz) where T : ISszCodec<T>
    {
        T.Decode(ssz, out T value);
        return value;
    }

    private static void ApplyGloasVoluntaryExit(OpContext<BeaconStateGloas> ctx, byte[] ssz, BlockSignatureBatch? batch)
    {
        SignedVoluntaryExit.Decode(ssz, out SignedVoluntaryExit value);
        GloasBlockProcessing.ProcessVoluntaryExit(ctx.State, value, ctx.Cache, ctx.Pubkeys, ctx.VerifySignatures, batch);
    }

    private sealed class FixedNewPayloadNotifier(bool valid) : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => valid ? ExecutionStatus.Valid : ExecutionStatus.Invalid;
    }

    internal static readonly Dictionary<string, (string File, Action<OpContext<BeaconStateFulu>, byte[], BlockSignatureBatch?> Apply)> FuluOperations =
        SignatureHandlers(Handlers, ["attestation", "attester_slashing", "bls_to_execution_change", "proposer_slashing", "sync_aggregate", "voluntary_exit"]);

    internal static readonly Dictionary<string, (string File, Action<OpContext<BeaconStateGloas>, byte[], BlockSignatureBatch?> Apply)> GloasOperations =
        SignatureHandlers(GloasHandlers, ["attestation", "attester_slashing", "bls_to_execution_change", "execution_payload_bid", "payload_attestation", "proposer_slashing", "sync_aggregate", "voluntary_exit", "voluntary_exit_churn"]);

    private static Dictionary<string, (string File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply)> SignatureHandlers<TState>(
        Dictionary<string, (string? File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply)> handlers, string[] names) =>
        names.ToDictionary(static name => name, name => (handlers[name].File!, handlers[name].Apply), StringComparer.Ordinal);
}
