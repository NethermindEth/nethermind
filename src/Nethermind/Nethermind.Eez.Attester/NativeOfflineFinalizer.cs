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
        BlockWitness[] blocks = operation.CopyBlocks();
        ReadOnlySpan<int> checkpoints = operation.NativeCheckpoints;
        if (blocks.Length == 0 || checkpoints.Length != blocks.Length) throw Rejected();
        try
        {
            NativeStreamingEvidence history = retained.Prefix(checkpoints[^1]);
            PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(settlement.AbiCalldata.Span);
            NativeOfflineContext offline = Context(batch, settlement.PostingContext, history);
            for (int index = 0; index < checkpoints.Length; index++) history.VerifyPrefix(checkpoints[index], offline.Anchor);
            ulong eligibility = offline.Anchor.TargetTimestamp ?? history.Endpoint.Timestamp;
            Begin predecessor = operation.Predecessor ?? throw Rejected();
            ValidateRange(predecessor.AnchorNumber, selection.ToBlock, eligibility, false, offline);
            RollupXSettlementInput input = RollupXSettlementInput.Decode(settlement, _context, _profile.L1.Specs, cancellation,
                offline, eligibility, batch, _profile.L1);
            using AdmittedWindow window = ToWindow(blocks, selection);
            EezStatelessBlockResult[] executed = Execute(operation, window, history, cancellation);
            BlockHeader parent = executed[0].Parent;
            if (parent.Timestamp != _profile.Genesis.Rules.Schedule!.Timestamp(parent.Number) ||
                parent.Number == 0 && parent.Hash?.ValueHash256 != _profile.Genesis.GenesisHash) throw Rejected();
            ValidateRange(parent.Number, executed[^1].Block.Number, eligibility, input.Stream.Messages.Length != 0, offline);
            RollupXObservationClaim[] observations = RollupXSettlementVerifier.VerifyNative(input.Batch, input.Stream, executed,
                _context, _profile.Genesis.Rules, offline, cancellation);
            if (observations.Length != 0)
                history.CreateObservationReplay(_profile.Genesis, _logs).Verify(window.Blocks[^1], executed[^1], observations, cancellation);
            if (!offline.UsesCore) ValidateAcceptedRoot(parent, executed[^1], offline.AcceptedL2BlockHash);
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
            WitnessValidationException or RlpException or ArgumentException or OverflowException)
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

    private EezStatelessBlockResult[] Execute(SessionOperation operation, AdmittedWindow window, NativeStreamingEvidence history,
        CancellationToken cancellation)
    {
        Begin predecessor = operation.Predecessor ?? throw Rejected();
        EezStatelessBlockResult[] executed = new EezStatelessBlockResult[window.Blocks.Length];
        EezStatelessExecutor executor = history.CreateExecutor(_profile.Genesis, _logs);
        for (int index = 0; index < executed.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            int[] positions = index == executed.Length - 1
                ? RollupXCheckpointPlan.Create(Rlp.Decode<Block>(window.Blocks[index].Rlp) ?? throw Rejected()).Positions.ToArray()
                : [];
            EezStatelessBlockResult result = executor.Execute([window.Blocks[index]], positions)[0];
            Hash256 parentHash = index == 0 ? new Hash256(predecessor.AnchorHash.Span) : executed[index - 1].Hash;
            Hash256 parentState = index == 0 ? new Hash256(predecessor.AnchorStateRoot.Span) : executed[index - 1].PostStateRoot;
            if (result.Block.Number != window.FromBlock + (ulong)index || result.Hash != window.Claims[index].Hash ||
                result.Block.ParentHash != parentHash || result.PreStateRoot != parentState ||
                !Rlp.Encode(result.Block).Bytes.AsSpan().SequenceEqual(window.Blocks[index].Rlp)) throw Rejected();
            executed[index] = result;
        }
        return executed;
    }

    private void ValidateRange(ulong parentNumber, ulong terminalNumber, ulong eligibility, bool hasActions, NativeOfflineContext offline)
    {
        RollupXSchedule schedule = _profile.Genesis.Rules.Schedule!;
        if (terminalNumber <= parentNumber || !schedule.IsSyncPosition(parentNumber) ||
            (terminalNumber - parentNumber) % schedule.SyncIntervalBlocks != 0 ||
            schedule.AnchorKind(terminalNumber, eligibility) is not { } kind ||
            hasActions && (kind == RollupXAnchorKind.CatchUp || offline.Anchor.TargetTimestamp is null)) throw Rejected();
    }

    private static void ValidateAcceptedRoot(BlockHeader parent, EezStatelessBlockResult terminal, ValueHash256 accepted)
    {
        if (parent.Hash?.ValueHash256 == accepted) return;
        foreach (EezTransactionCheckpoint checkpoint in terminal.Checkpoints)
            if (checkpoint.BlockHash.ValueHash256 == accepted) return;
        throw Rejected();
    }

    private static AdmittedWindow ToWindow(BlockWitness[] blocks, Finalize selection)
    {
        EezStatelessBlock[] witnessed = new EezStatelessBlock[blocks.Length];
        (Hash256 Hash, Hash256 ParentHash)[] claims = new (Hash256, Hash256)[blocks.Length];
        int count = 0;
        try
        {
            for (; count < blocks.Length; count++)
            {
                BlockWitness block = blocks[count];
                witnessed[count] = new(block.Rlp.ToByteArray(), WindowAssembler.ToL2Witness(block.Witness));
                claims[count] = (new Hash256(block.Hash.Span), new Hash256(block.ParentHash.Span));
            }
            return new AdmittedWindow(selection.FromBlock, selection.ToBlock, [], witnessed, claims);
        }
        catch
        {
            for (int index = 0; index < count; index++) witnessed[index].Witness.Dispose();
            throw;
        }
    }

    private static RpcException Rejected() => new(new Status(StatusCode.FailedPrecondition, "RollupX settlement validation rejected"));
    private static RpcException Invalid() => new(new Status(StatusCode.InvalidArgument, "invalid RollupX settlement input"));
}
