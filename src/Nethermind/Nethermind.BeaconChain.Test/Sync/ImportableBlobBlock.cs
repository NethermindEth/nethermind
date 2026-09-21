// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz.Merkleization;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// A genesis-like Fulu anchor and one fully valid, genuinely signed child block carrying blob
/// commitments, plus the real column sidecars for it (real KZG cells and proofs, and an inclusion
/// proof folded from the block body's own field roots). Every check the import pipeline runs before
/// data availability - proposer and RANDAO signatures, payload consistency, the post-state root -
/// passes, so the availability gate is the only thing left that can reject this block.
/// </summary>
internal sealed class ImportableBlobBlock
{
    private const ulong Gwei = 1_000_000_000;
    private const int ValidatorCount = 16;
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

    /// <summary>
    /// Mainnet parameters with Electra and Fulu live from genesis, so the spec agrees that the slot-0
    /// anchor is a Fulu state. Under mainnet's own fork epochs every slot of this fixture sits below
    /// the <see cref="DataAvailabilityBoundary"/> (floored at <c>FULU_FORK_EPOCH</c>) and no column
    /// would ever be demanded for <see cref="Block"/>.
    /// </summary>
    public static BeaconChainSpec FuluFromGenesis { get; } = new()
    {
        ChainId = BeaconChainSpec.Mainnet.ChainId,
        CheckpointSyncUrl = BeaconChainSpec.Mainnet.CheckpointSyncUrl,
        Bootnodes = BeaconChainSpec.Mainnet.Bootnodes,
        SecondsPerSlot = BeaconChainSpec.Mainnet.SecondsPerSlot,
        SlotsPerEpoch = BeaconChainSpec.Mainnet.SlotsPerEpoch,
        GenesisTime = BeaconChainSpec.Mainnet.GenesisTime,
        GenesisValidatorsRoot = BeaconChainSpec.Mainnet.GenesisValidatorsRoot,
        Forks = BeaconChainSpec.Mainnet.Forks,
        BlobSchedule = BeaconChainSpec.Mainnet.BlobSchedule,
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
        MaxBlobsPerBlockElectra = BeaconChainSpec.Mainnet.MaxBlobsPerBlockElectra,
        GloasForkEpoch = BeaconChainSpec.Mainnet.GloasForkEpoch,
        GloasForkVersion = BeaconChainSpec.Mainnet.GloasForkVersion,
    };

    public BeaconChainSpec Spec => FuluFromGenesis;

    public required BeaconStateFulu AnchorState { get; init; }

    public required SignedBeaconBlock AnchorBlock { get; init; }

    public required Hash256 AnchorRoot { get; init; }

    /// <summary>Built from the anchor registry, as <c>BeaconChainService</c> does before the importer runs.</summary>
    public required PubkeyCache Pubkeys { get; init; }

    public required SignedBeaconBlock Block { get; init; }

    public required Hash256 BlockRoot { get; init; }

    /// <summary>All <see cref="Eip7594DasConstants.NumberOfColumns"/> sidecars of <see cref="Block"/>, indexed by column.</summary>
    public required DataColumnSidecar[] Columns { get; init; }

    /// <summary>A wall clock stopped at the first slot of <paramref name="epoch"/> under <see cref="Spec"/>, for placing <see cref="Block"/> inside or below the data availability window.</summary>
    public SlotClock ClockAtEpoch(ulong epoch) =>
        new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + epoch * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime));

    public static ImportableBlobBlock Create(int blobCount = 2)
    {
        BlsPublicKey[] pubkeys = new BlsPublicKey[ValidatorCount];
        for (int i = 0; i < pubkeys.Length; i++)
        {
            pubkeys[i] = new BlsPublicKey(new Bls.P1(DeriveKey(i)).Compress());
        }

        BeaconStateFulu anchorState = CreateState(pubkeys);
        SignedBeaconBlock anchorBlock = TestChain.CreateBlock(slot: 0, parentRoot: Hash(0x02));
        BeaconBlock anchorMessage = anchorBlock.Message!;
        anchorMessage.Body!.ExecutionPayload!.BlockHash = anchorState.LatestExecutionPayloadHeader!.BlockHash;
        // Genesis shape: the header's state root is zero until the first process_slot fills it in,
        // which is what makes hash_tree_root(latest_block_header) equal the anchor block root.
        anchorState.LatestBlockHeader = new BeaconBlockHeader
        {
            Slot = 0,
            ProposerIndex = anchorMessage.ProposerIndex,
            ParentRoot = anchorMessage.ParentRoot,
            StateRoot = Hash256.Zero,
            BodyRoot = SszRoots.HashTreeRoot(anchorMessage.Body),
        };
        anchorMessage.StateRoot = SszRoots.HashTreeRoot(anchorState);
        Hash256 anchorRoot = SszRoots.HashTreeRoot(anchorMessage);

        PubkeyCache pubkeyCache = new();
        pubkeyCache.Build(anchorState.Validators!);

        DataColumnKzgFixture.BlobFixture[] blobs = [.. Enumerable.Range(0, blobCount).Select(i => DataColumnKzgFixture.BuildBlob((byte)(0x10 * (i + 1))))];
        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];

        // The lookahead is all zeros, so validator 0 proposes slot 1.
        Bls.SecretKey proposerKey = DeriveKey(0);
        BeaconBlock block = TestChain.CreateBlock(slot: 1, parentRoot: anchorRoot).Message!;
        block.ProposerIndex = 0;
        BeaconBlockBody body = block.Body!;
        body.RandaoReveal = Sign(proposerKey, EpochRoot(0), anchorState.GetDomain(DomainType.Randao, 0));
        body.Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero };
        body.SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize), SyncCommitteeSignature = new BlsSignature(SignatureSets.G2PointAtInfinity) };
        body.BlobKzgCommitments = commitments;
        ExecutionPayload payload = body.ExecutionPayload!;
        payload.ParentHash = anchorState.LatestExecutionPayloadHeader.BlockHash;
        payload.PrevRandao = anchorState.GetRandaoMix(0);
        payload.Timestamp = anchorState.GenesisTime + Presets.SecondsPerSlot;
        payload.BlockNumber = 1;
        payload.BlockHash = Hash(0x81);

        BeaconStateFulu postState = anchorState.Clone();
        FuluStateTransition.Apply(postState, new SignedBeaconBlock { Message = block }, new EpochCache(), pubkeyCache, new AcceptingNotifier(), FuluFromGenesis, validateResult: false, verifySignatures: false);
        block.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        BlsSignature signature = Sign(proposerKey, blockRoot, anchorState.GetDomain(DomainType.BeaconProposer, 0));

        Hash256[] inclusionProof = KzgCommitmentsInclusionProof(body);
        DataColumnSidecar[] columns = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int column = 0; column < columns.Length; column++)
        {
            columns[column] = new DataColumnSidecar
            {
                Index = (ulong)column,
                Column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, column))],
                KzgCommitments = commitments,
                KzgProofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, column))],
                SignedBlockHeader = new SignedBeaconBlockHeader
                {
                    Message = new BeaconBlockHeader
                    {
                        Slot = block.Slot,
                        ProposerIndex = block.ProposerIndex,
                        ParentRoot = block.ParentRoot,
                        StateRoot = block.StateRoot,
                        BodyRoot = SszRoots.HashTreeRoot(body),
                    },
                    Signature = signature,
                },
                KzgCommitmentsInclusionProof = inclusionProof,
            };
        }

        return new ImportableBlobBlock
        {
            AnchorState = anchorState,
            AnchorBlock = anchorBlock,
            AnchorRoot = anchorRoot,
            Pubkeys = pubkeyCache,
            Block = new SignedBeaconBlock { Message = block, Signature = signature },
            BlockRoot = blockRoot,
            Columns = columns,
        };
    }

    /// <summary>A block at slot 1 with no blob commitments, otherwise identical in validity to <see cref="Block"/>.</summary>
    public static ImportableBlobBlock CreateWithoutBlobs() => Create(blobCount: 0);

    private static Bls.SecretKey DeriveKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)index));

    private static BlsSignature Sign(Bls.SecretKey key, Hash256 objectRoot, Hash256 domain) =>
        new(BlsSigner.Sign(key, Domains.ComputeSigningRoot(objectRoot, domain).Bytes).Bytes);

    /// <summary><c>hash_tree_root(epoch)</c>: one little-endian uint64 chunk.</summary>
    private static Hash256 EpochRoot(ulong epoch)
    {
        byte[] root = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(root, epoch);
        return new Hash256(root);
    }

    /// <summary>
    /// The depth-4 branch proving <c>blob_kzg_commitments</c> (field 11 of the 13-field body) against
    /// <c>hash_tree_root(body)</c>, folded here from each field's own root rather than taken from the
    /// codec, so a sidecar built from it is only valid if the body really merkleizes this way. The
    /// operation lists are empty in this fixture, which is what the fixed empty-list roots assume.
    /// </summary>
    private static Hash256[] KzgCommitmentsInclusionProof(BeaconBlockBody body)
    {
        Hash256[] level = new Hash256[1 << Eip7594DasConstants.KzgCommitmentsInclusionProofDepth];
        level[0] = ChunkRoot(body.RandaoReveal.Bytes);
        level[1] = SszRoots.HashTreeRoot(body.Eth1Data!);
        level[2] = body.Graffiti!;
        level[3] = EmptyListRoot(16);
        level[4] = EmptyListRoot(1);
        level[5] = EmptyListRoot(8);
        level[6] = EmptyListRoot(16);
        level[7] = EmptyListRoot(16);
        level[8] = SszRoots.HashTreeRoot(body.SyncAggregate!);
        level[9] = SszRoots.HashTreeRoot(body.ExecutionPayload!);
        level[10] = EmptyListRoot(16);
        level[11] = DataColumnSidecarVerifier.ComputeCommitmentsListRoot(body.BlobKzgCommitments!);
        level[12] = SszRoots.HashTreeRoot(body.ExecutionRequests!);
        for (int i = 13; i < level.Length; i++)
        {
            level[i] = Hash256.Zero;
        }

        Hash256[] branch = new Hash256[Eip7594DasConstants.KzgCommitmentsInclusionProofDepth];
        int index = Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex;
        for (int depth = 0; depth < branch.Length; depth++)
        {
            branch[depth] = level[index ^ 1];
            Hash256[] parents = new Hash256[level.Length / 2];
            for (int i = 0; i < parents.Length; i++)
            {
                parents[i] = HashPair(level[2 * i], level[2 * i + 1]);
            }

            level = parents;
            index >>= 1;
        }

        if (level[0] != SszRoots.HashTreeRoot(body))
        {
            throw new InvalidOperationException("The fixture's hand-folded body root disagrees with the SSZ codec; the inclusion proof would be wrong.");
        }

        return branch;
    }

    private static Hash256 HashPair(Hash256 left, Hash256 right)
    {
        byte[] combined = new byte[64];
        left.Bytes.CopyTo(combined);
        right.Bytes.CopyTo(combined.AsSpan(32));
        return new Hash256(SHA256.HashData(combined));
    }

    private static Hash256 ChunkRoot(ReadOnlySpan<byte> value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return new Hash256(root.ToLittleEndian());
    }

    private static Hash256 EmptyListRoot(ulong limit)
    {
        Merkle.Merkleize(out UInt256 root, ReadOnlySpan<UInt256>.Empty, limit);
        Merkle.MixIn(ref root, 0);
        return new Hash256(root.ToLittleEndian());
    }

    private static BeaconStateFulu CreateState(BlsPublicKey[] pubkeys)
    {
        Validator[] validators = new Validator[pubkeys.Length];
        ulong[] balances = new ulong[pubkeys.Length];
        for (int i = 0; i < validators.Length; i++)
        {
            validators[i] = new Validator
            {
                Pubkey = pubkeys[i],
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = 32 * Gwei,
                ActivationEpoch = 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
                ActivationEligibilityEpoch = 0,
            };
            balances[i] = 32 * Gwei;
        }

        // Every committee seat must map back to a registered validator for sync aggregate rewards.
        BlsPublicKey[] committee = new BlsPublicKey[Presets.SyncCommitteeSize];
        for (int i = 0; i < committee.Length; i++)
        {
            committee[i] = pubkeys[i % pubkeys.Length];
        }

        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x42));
        Hash256[] blockRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(blockRoots, Hash256.Zero);
        Hash256[] stateRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(stateRoots, Hash256.Zero);

        return new BeaconStateFulu
        {
            GenesisTime = 1_700_000_000,
            GenesisValidatorsRoot = Hash(0x01),
            Slot = 0,
            Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 },
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            HistoricalRoots = [],
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Eth1DataVotes = [],
            Eth1DepositIndex = 0,
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[validators.Length],
            CurrentEpochParticipation = new byte[validators.Length],
            JustificationBits = new BitArray(4),
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            InactivityScores = new ulong[validators.Length],
            CurrentSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = pubkeys[0] },
            NextSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = pubkeys[0] },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ParentHash = Hash(0x70), BlockHash = Hash(0x71), PrevRandao = Hash(0x72), GasLimit = 30_000_000, ExtraData = [] },
            NextWithdrawalIndex = 0,
            NextWithdrawalValidatorIndex = 0,
            HistoricalSummaries = [],
            DepositRequestsStartIndex = Presets.UnsetDepositRequestsStartIndex,
            PendingDeposits = [],
            PendingPartialWithdrawals = [],
            PendingConsolidations = [],
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
        };
    }

    private static Hash256 Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return new Hash256(bytes);
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
