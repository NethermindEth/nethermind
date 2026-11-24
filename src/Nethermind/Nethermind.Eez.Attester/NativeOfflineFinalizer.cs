// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using PostBatch = Nethermind.Eez.Execution.Settlement.PostBatch;
using L1PostingContext = Nethermind.Eez.Prove.V2.L1PostingContext;
using Block = Nethermind.Core.Block;
using Finalize = Nethermind.Eez.Prove.V2.Finalize;

namespace Nethermind.Eez.Attester;

internal sealed class NativeOfflineFinalizer
{
    private readonly NativeStreamingProfile _profile;
    private readonly EezSettlementContext _context;
    private readonly Address _registry;
    private readonly Address _manager;
    private readonly EezAttestationSigner _signer;
    private readonly ILogManager _logs;

    internal NativeOfflineFinalizer(NativeStreamingProfile profile, EezSettlementContext context,
        Address registry, Address manager, EezAttestationSigner signer, ILogManager logs)
    {
        if (profile.Genesis.Rules.NativeFormat is not (NativeSystemTxFormat.ContractV4 or NativeSystemTxFormat.ContractV6 or NativeSystemTxFormat.ContractV7) ||
            profile.Genesis.Specs.ChainId != context.ChainId || registry == Address.Zero || manager == Address.Zero)
            throw new ArgumentException("Offline finalization requires a matching native offline deployment.");
        NativeOfflineProfile.Inputs network = profile.Offline;
        if (network.ValidationVersion == 7)
        {
            ValueHash256 commitment = NativeNetworkProfile.Compute(in network);
            if (NativeNetworkProfile.VerificationKey(in commitment, registry, context.ProofSystem, signer.Address) != context.VerificationKey)
                throw new ArgumentException("Configured verification key differs from the local network profile and signer.");
        }
        _profile = profile;
        _context = context;
        _registry = registry;
        _manager = manager;
        _signer = signer;
        _logs = logs;
    }

    internal Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation) =>
        Task.Run(() => Validate(operation, cancellation), CancellationToken.None);

    private Proof Validate(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!ReferenceEquals(operation.NativeProfile, _profile) || operation.NativeHistory is not { } retained)
            throw Rejected();
        Finalize selection = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
        SettlementInput settlement = selection.Settlement ?? throw Invalid();
        ReadOnlySpan<ValidatedRollupXBlock> blocks = operation.NativeBlocks;
        BlockWitness terminalInput = operation.NativeTerminal ?? throw Rejected();
        if (blocks.IsEmpty) throw Rejected();
        try
        {
            ref readonly ValidatedRollupXBlock terminalBlock = ref blocks[^1];
            NativeStreamingEvidence history = retained.Prefix(terminalBlock.L1CheckpointLength);
            PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(settlement.AbiCalldata.Span);
            NativeOfflineContext offline = Context(batch, settlement.PostingContext, history);
            ulong eligibility = offline.Anchor.TargetTimestamp ?? history.Endpoint.Timestamp;
            Begin predecessor = operation.Predecessor ?? throw Rejected();
            ValueHash256 parentHash = new(predecessor.AnchorHash.Span);
            ValidateBlocks(blocks, predecessor, history, cancellation);
            if (blocks[0].ParentTimestamp != _profile.Genesis.Rules.Schedule!.Timestamp(predecessor.AnchorNumber) ||
                predecessor.AnchorNumber == 0 && parentHash != _profile.Genesis.GenesisHash) throw Rejected();
            ValidateRange(predecessor.AnchorNumber, selection.ToBlock, eligibility, false, offline);
            RollupXSettlementInput input = RollupXSettlementInput.Decode(settlement, _context, _profile.L1.Specs, cancellation,
                offline, eligibility, batch, _profile.L1);
            using Witness witness = WindowAssembler.ToL2Witness(terminalInput.Witness);
            EezStatelessBlock terminalWitness = new(terminalInput.Rlp.ToByteArray(), witness);
            EezStatelessBlockResult terminal = ReplayTerminal(terminalWitness, in terminalBlock, history, cancellation);
            ValidateRange(predecessor.AnchorNumber, terminal.Block.Number, eligibility, input.Stream.Messages.Length != 0, offline);
            RollupXObservationClaim[] observations = RollupXSettlementVerifier.VerifyNative(input.Batch, input.Stream, blocks[..^1], terminal,
                parentHash, _context, _profile.Genesis.Rules, offline, cancellation);
            if (observations.Length != 0)
                history.CreateObservationReplay(_profile.Genesis, _logs).Verify(terminalWitness, terminal, observations, cancellation);
            if (!offline.UsesCore) ValidateAcceptedRoot(parentHash, terminal, offline.AcceptedL2BlockHash);
            ValueHash256 publicInputs = RollupXPostBatchProfile.PublicInputsHash(input.Batch, _context.RollupId,
                _context.VerificationKey, input.BlobHashes, offline.CustomData);
            cancellation.ThrowIfCancellationRequested();
            return operation.SignCurrent(_signer, publicInputs, cancellation);
        }
        catch (EezAbiException)
        {
            throw Invalid();
        }
        catch (Exception exception) when (exception is EezSettlementException or EezStatelessException or
            WitnessValidationException or RlpReaderException or ArgumentException or OverflowException)
        {
            throw Rejected();
        }
    }

    private NativeOfflineContext Context(PostBatch batch, L1PostingContext? posting, NativeStreamingEvidence history)
    {
        NativeEvidenceAnchor anchor;
        if (batch.BlockNumber == ulong.MaxValue)
        {
            if (posting is null) throw Rejected();
            if (posting.ChainId == 0 || posting.TargetTimestamp == 0 || posting.ParentNumber == ulong.MaxValue ||
                posting.ParentHash.Length != Hash256.Size || !posting.ParentHash.Span.ContainsAnyExcept((byte)0)) throw Invalid();
            if (posting.ChainId != _profile.L1.Specs.ChainId) throw Rejected();
            if (_profile.L1.Headers.UnsupportedTime is { } unsupported && posting.TargetTimestamp >= unsupported) throw Invalid();
            anchor = new(posting.ParentNumber, new ValueHash256(posting.ParentHash.Span), posting.TargetTimestamp);
        }
        else
        {
            if (batch.BlockNumber == 0 || posting is not null) throw Rejected();
            anchor = new(batch.BlockNumber, history.Endpoint.Hash, null);
        }
        history.VerifyAnchor(in anchor);
        if (batch.ExpectedRoots is not [{ } pin] || pin.RollupId != _context.RollupId || pin.Root == default) throw Rejected();
        NativeOfflineProfile.Inputs profile = _profile.Offline;
        return new NativeOfflineContext(in profile, _profile.L1.RuleTimes, _registry, _manager,
            _context.RollupId, pin.Root, in anchor, history.Endpoint);
    }

    private static void ValidateBlocks(ReadOnlySpan<ValidatedRollupXBlock> blocks, Begin predecessor,
        NativeStreamingEvidence history, CancellationToken cancellation)
    {
        ulong parentNumber = predecessor.AnchorNumber;
        ValueHash256 parentHash = new(predecessor.AnchorHash.Span);
        ValueHash256 parentState = new(predecessor.AnchorStateRoot.Span);
        foreach (ref readonly ValidatedRollupXBlock block in blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (parentNumber == ulong.MaxValue || block.Number != parentNumber + 1 || block.ParentNumber != parentNumber ||
                block.ParentHash != parentHash || block.PreStateRoot != parentState) throw Rejected();
            history.VerifyPrefix(block.L1CheckpointLength, block.L1CheckpointHash);
            parentNumber = block.Number;
            parentHash = block.Hash;
            parentState = block.PostStateRoot;
        }
    }

    private EezStatelessBlockResult ReplayTerminal(EezStatelessBlock witness, in ValidatedRollupXBlock block,
        NativeStreamingEvidence history, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Rlp.Decode<Block>(witness.Rlp) ?? throw Rejected());
        EezStatelessExecutor executor = history.CreateExecutor(_profile.Genesis, _logs);
        EezStatelessBlockResult result = executor.Execute([witness], plan.Positions)[0];
        if (result.Block.Number != block.Number || result.Hash.ValueHash256 != block.Hash ||
            result.Block.ParentHash?.ValueHash256 != block.ParentHash || result.PreStateRoot.ValueHash256 != block.PreStateRoot ||
            !Rlp.Encode(result.Block).AsSpan().SequenceEqual(witness.Rlp)) throw Rejected();
        return result;
    }

    private void ValidateRange(ulong parentNumber, ulong terminalNumber, ulong eligibility, bool hasActions, NativeOfflineContext offline)
    {
        RollupXSchedule schedule = _profile.Genesis.Rules.Schedule!;
        if (terminalNumber <= parentNumber || !schedule.IsSyncPosition(parentNumber) ||
            (terminalNumber - parentNumber) % schedule.SyncIntervalBlocks != 0 ||
            schedule.AnchorKind(terminalNumber, eligibility) is not { } kind ||
            hasActions && (kind == RollupXAnchorKind.CatchUp || offline.Anchor.TargetTimestamp is null)) throw Rejected();
    }

    private static void ValidateAcceptedRoot(ValueHash256 parentHash, EezStatelessBlockResult terminal, ValueHash256 accepted)
    {
        if (parentHash == accepted) return;
        foreach (EezTransactionCheckpoint checkpoint in terminal.Checkpoints)
            if (checkpoint.BlockHash.ValueHash256 == accepted) return;
        throw Rejected();
    }

    private static RpcException Rejected() => new(new Status(StatusCode.FailedPrecondition, "RollupX settlement validation rejected"));
    private static RpcException Invalid() => new(new Status(StatusCode.InvalidArgument, "invalid RollupX settlement input"));
}
