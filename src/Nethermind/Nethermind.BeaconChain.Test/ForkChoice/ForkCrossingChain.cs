// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>States are frozen by sealed root; fork-choice cases share them but each owns its runner.</summary>
internal sealed class ForkCrossingChain : IForkChoiceStateProvider, IGloasBlockStateProvider
{
    public const ulong ForkEpoch = 1;

    public const int VotingSlotCount = 24;

    private static readonly Lazy<ForkCrossingChain> Shared = new(static () => new ForkCrossingChain());

    private readonly Dictionary<Hash256, BeaconStateFulu> _fuluStates = [];
    private readonly Dictionary<Hash256, BeaconStateGloas> _gloasStates = [];

    private ForkCrossingChain()
    {
        AnchorState = CreateAnchorState(out BeaconBlock anchorBlock);
        AnchorBlock = anchorBlock;
        AnchorRoot = SszRoots.HashTreeRoot(anchorBlock);
        _fuluStates[AnchorRoot] = AnchorState;

        EpochCache cache = new();
        BeaconStateFulu fulu = AnchorState.Clone();
        SlotProcessing.ProcessSlots(fulu, BoundarySlot, cache);
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(fulu, Spec);

        // A last-Fulu-epoch vote carried in the Gloas container: its target checkpoint state is Fulu.
        CommitteeCache epoch0 = cache.GetCommitteeCache(state, 0);
        First = Seal(state, cache, 0xF0, [CommitteeAttestation(state, VoteFor(state, BoundarySlot - 1, 0, AnchorRoot), epoch0, 0, sign: false)]);
        if (First.Block.Message!.ParentRoot != AnchorRoot)
            throw new InvalidOperationException("Fixture bug: the anchor state's latest header does not hash to the anchor block root");

        List<ChainBlock> voting = [];
        for (int i = 0; i < VotingSlotCount / 8; i++)
        {
            GloasSlotProcessing.ProcessSlots(state, 2 * Presets.SlotsPerEpoch + (ulong)i, cache);
            CommitteeCache epoch1 = cache.GetCommitteeCache(state, ForkEpoch);
            AttestationGloas[] votes = new AttestationGloas[8];
            for (int j = 0; j < votes.Length; j++)
            {
                ulong slot = BoundarySlot + (ulong)(8 * i + j);
                votes[j] = CommitteeAttestation(state, VoteFor(state, slot, ForkEpoch, First.Root), epoch1, 0, sign: false);
            }

            voting.Add(Seal(state, cache, (byte)(0xF1 + i), votes));
        }

        Voting = voting;
        CommitteeCache epoch1Committees = cache.GetCommitteeCache(First.PostState, ForkEpoch);
        Committee32 = [.. epoch1Committees.GetBeaconCommittee(BoundarySlot, 0).ToArray().Select(static i => (ulong)i).Order()];

        HashSet<int> gloasVoters = [];
        for (int i = 0; i < VotingSlotCount; i++)
        {
            gloasVoters.UnionWith(epoch1Committees.GetBeaconCommittee(BoundarySlot + (ulong)i, 0).ToArray());
        }

        LastFuluVotesStanding = epoch0.GetBeaconCommittee(BoundarySlot - 1, 0).ToArray().Count(i => !gloasVoters.Contains(i));
    }

    public sealed record ChainBlock(SignedBeaconBlockGloas Block, Hash256 Root, BeaconStateGloas PostState);

    public static ForkCrossingChain Instance => Shared.Value;
    public BeaconChainSpec Spec { get; } = SyntheticSpec(ForkEpoch);
    public BeaconStateFulu AnchorState { get; }
    public BeaconBlock AnchorBlock { get; }
    public Hash256 AnchorRoot { get; }
    public ChainBlock First { get; }
    public IReadOnlyList<ChainBlock> Voting { get; }
    public ulong[] Committee32 { get; }
    /// <summary>These slot-31 voters never cast a later vote that would replace their latest message.</summary>
    public int LastFuluVotesStanding { get; }

    public ForkChoiceRunner CreateRunner(bool withGloasStates = true, PubkeyCache? pubkeys = null)
    {
        if (pubkeys is null)
        {
            pubkeys = new PubkeyCache();
            pubkeys.Build(AnchorState.Validators!);
        }

        return new ForkChoiceRunner(Spec, AnchorState, AnchorBlock, this, pubkeys, withGloasStates ? this : null);
    }

    public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => _fuluStates.GetValueOrDefault(blockRoot);
    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
    public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => _gloasStates.GetValueOrDefault(blockRoot);

    public BeaconStateGloas UpgradedAnchor()
    {
        BeaconStateFulu fulu = AnchorState.Clone();
        SlotProcessing.ProcessSlots(fulu, BoundarySlot, new EpochCache());
        return GloasForkTransition.UpgradeToGloas(fulu, Spec);
    }

    private static BeaconStateFulu CreateAnchorState(out BeaconBlock anchorBlock)
    {
        BeaconStateFulu state = CreateFuluState(ValidatorCount);
        state.FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero };
        Validator[] validators = state.Validators!;
        InstallValidatorKeys(validators, validators.Length);

        BlsPublicKey[] syncCommittee = Enumerable.Repeat(validators[0].Pubkey, Presets.SyncCommitteeSize).ToArray();
        BlsPublicKey aggregatePubkey = AggregatePubkey(syncCommittee);
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = syncCommittee, AggregatePubkey = aggregatePubkey };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = syncCommittee, AggregatePubkey = aggregatePubkey };

        anchorBlock = TestChain.CreateBlock(0, Hash256.Zero).Message!;
        state.LatestBlockHeader = ImportableBlobBlock.HeaderFor(anchorBlock).Message!;
        anchorBlock.StateRoot = SszRoots.HashTreeRoot(state);
        return state;
    }

    /// <summary>Anchor sync-committee aggregate keys must satisfy Altair eth_aggregate_pubkeys.</summary>
    private static BlsPublicKey AggregatePubkey(BlsPublicKey[] pubkeys)
    {
        BlsSigner.AggregatedPublicKey aggregate = new();
        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        foreach (BlsPublicKey pubkey in pubkeys)
        {
            publicKey.Decode(pubkey.Bytes);
            aggregate.Aggregate(publicKey);
        }

        return new BlsPublicKey(aggregate.PublicKey.Compress());
    }

    private ChainBlock Seal(BeaconStateGloas state, EpochCache cache, byte blockHashFill, AttestationGloas[] attestations)
    {
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(blockHashFill)));
        BeaconBlockGloas message = block.Message!;
        message.Body!.Attestations = attestations;
        GloasBlockProcessing.ProcessBlock(state, message, cache, new PubkeyCache(), new AcceptingNotifier(), Spec, verifySignatures: false);
        message.StateRoot = SszRoots.HashTreeRoot(state);

        Hash256 root = SszRoots.HashTreeRoot(message);
        BeaconStateGloas frozen = state.Clone();
        _gloasStates[root] = frozen;
        return new ChainBlock(block, root, frozen);
    }
}
