// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz.Merkleization;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>The signed block passes every pre-availability check; only missing columns can defer it.</summary>
internal sealed class ImportableBlobBlock
{
    private const int ValidatorCount = 16;

    /// <summary>Activate Fulu at genesis so fixture blocks fall inside the availability window.</summary>
    public static BeaconChainSpec FuluFromGenesis { get; } = BeaconChainSpec.Mainnet with
    {
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
    };

    public BeaconChainSpec Spec => FuluFromGenesis;
    public required BeaconStateFulu AnchorState { get; init; }
    public required SignedBeaconBlock AnchorBlock { get; init; }
    public required Hash256 AnchorRoot { get; init; }
    public required PubkeyCache Pubkeys { get; init; }
    public required SignedBeaconBlock Block { get; init; }
    public required Hash256 BlockRoot { get; init; }
    public required DataColumnSidecar[] Columns { get; init; }
    public SlotClock ClockAtEpoch(ulong epoch) => ClockAtSlot(epoch * Spec.SlotsPerEpoch);

    public SlotClock ClockAtSlot(ulong slot) =>
        new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot)).UtcDateTime));

    public static ImportableBlobBlock Create(int blobCount = 2, int validatorCount = ValidatorCount)
    {
        BlsPublicKey[] pubkeys = Enumerable.Range(0, validatorCount).Select(static i =>
            new BlsPublicKey(new Bls.P1(GloasTestFixtures.DeriveKey(i)).Compress())).ToArray();

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

        Bls.SecretKey proposerKey = GloasTestFixtures.DeriveKey(0);
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
        FuluStateTransition.Apply(postState, new SignedBeaconBlock { Message = block }, new EpochCache(), pubkeyCache, new TestEngineDriver.BodyOnlyNotifier(), FuluFromGenesis, validateResult: false, verifySignatures: false);
        block.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        BlsSignature signature = Sign(proposerKey, blockRoot, anchorState.GetDomain(DomainType.BeaconProposer, 0));

        DataColumnSidecar[] columns = BuildColumns(block, signature, blobs, commitments);

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

    /// <summary>These blocks have valid sidecar proofs but no valid state transition.</summary>
    public static (SignedBeaconBlock Block, Hash256 Root, DataColumnSidecar[] Columns) BlobBlockAt(ulong slot, Hash256 parentRoot, int blobCount = 1)
    {
        DataColumnKzgFixture.BlobFixture[] blobs = [.. Enumerable.Range(0, blobCount).Select(i => DataColumnKzgFixture.BuildBlob((byte)(0x10 * (i + 1))))];
        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];
        SignedBeaconBlock signed = TestChain.CreateBlock(slot, parentRoot);
        signed.Message!.Body!.RandaoReveal = new BlsSignature(SignatureSets.G2PointAtInfinity);
        signed.Message.Body.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(SignatureSets.G2PointAtInfinity);
        signed.Message.Body.BlobKzgCommitments = commitments;
        signed.Signature = new BlsSignature(SignatureSets.G2PointAtInfinity);
        return (signed, SszRoots.HashTreeRoot(signed.Message), BuildColumns(signed.Message, signed.Signature, blobs, commitments));
    }

    internal static SignedBeaconBlockHeader HeaderFor(BeaconBlock block, BlsSignature signature = default) => new()
    {
        Message = new BeaconBlockHeader
        {
            Slot = block.Slot,
            ProposerIndex = block.ProposerIndex,
            ParentRoot = block.ParentRoot,
            StateRoot = block.StateRoot,
            BodyRoot = SszRoots.HashTreeRoot(block.Body!),
        },
        Signature = signature,
    };

    private static DataColumnSidecar[] BuildColumns(BeaconBlock block, BlsSignature signature, DataColumnKzgFixture.BlobFixture[] blobs, SszKzgCommitment[] commitments)
    {
        BeaconBlockBody body = block.Body!;
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
                SignedBlockHeader = HeaderFor(block, signature),
                KzgCommitmentsInclusionProof = inclusionProof,
            };
        }

        return columns;
    }

    public static ImportableBlobBlock CreateWithoutBlobs() => Create(blobCount: 0);

    internal static BlsSignature Sign(Bls.SecretKey key, Hash256 objectRoot, Hash256 domain) =>
        GloasTestFixtures.Sign(key, Domains.ComputeSigningRoot(objectRoot, domain));

    internal static BlsSignature SignAs(ulong validatorIndex, Hash256 objectRoot, Hash256 domain) => Sign(GloasTestFixtures.DeriveKey((int)validatorIndex), objectRoot, domain);

    internal static Hash256 EpochRoot(ulong epoch)
    {
        byte[] root = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(root, epoch);
        return new Hash256(root);
    }

    /// <summary>Build the depth-4 commitments proof from independent body-field roots; fixed empty-list roots assume empty operations.</summary>
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
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(pubkeys.Length);
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            validators[i] = GloasTestFixtures.CreateActiveValidator(pubkeys[i]);
        }

        // Every committee seat must map back to a registered validator for sync aggregate rewards.
        BlsPublicKey[] committee = new BlsPublicKey[Presets.SyncCommitteeSize];
        for (int i = 0; i < committee.Length; i++)
        {
            committee[i] = pubkeys[i % pubkeys.Length];
        }

        state.GenesisTime = 1_700_000_000;
        state.GenesisValidatorsRoot = Hash(0x01);
        state.LatestBlockHeader = null;
        state.FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero };
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = pubkeys[0] };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = pubkeys[0] };
        state.LatestExecutionPayloadHeader!.ExtraData = [];
        state.HistoricalSummaries = [];
        state.DepositRequestsStartIndex = Presets.UnsetDepositRequestsStartIndex;
        return state;
    }

    private static Hash256 Hash(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}
