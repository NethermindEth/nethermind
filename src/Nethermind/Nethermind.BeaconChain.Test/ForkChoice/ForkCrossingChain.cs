// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// A real chain across the Gloas fork (<see cref="ForkEpoch"/> = 1): a genesis-like Fulu anchor at slot 0,
/// the first Gloas block at the boundary slot 32, and three epoch-2 blocks whose bodies carry enough
/// epoch-1 target votes (24 of the 32 one-committee slots, 1536 of 2048 validators) for the last one's
/// post-state to pull the justified checkpoint up to that first Gloas block. Every block runs through the
/// real Fulu or Gloas pipeline, signatures unverified; every state is frozen under the root it sealed.
/// </summary>
/// <remarks>
/// Built once per test run: nothing in fork choice mutates a provider state, so the tests share it and
/// each creates its own runner through <see cref="CreateRunner"/>.
/// </remarks>
internal sealed class ForkCrossingChain : IForkChoiceStateProvider, IGloasBlockStateProvider
{
    public const ulong ForkEpoch = 1;

    /// <summary>The epoch-1 slots whose committees vote, in groups of eight per epoch-2 block (the Electra body limit).</summary>
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

    /// <summary>A block of this chain with its frozen post-state.</summary>
    public sealed record ChainBlock(SignedBeaconBlockGloas Block, Hash256 Root, BeaconStateGloas PostState);

    public static ForkCrossingChain Instance => Shared.Value;

    public BeaconChainSpec Spec { get; } = SyntheticSpec(ForkEpoch);

    public BeaconStateFulu AnchorState { get; }

    public BeaconBlock AnchorBlock { get; }

    public Hash256 AnchorRoot { get; }

    /// <summary>The first Gloas block, at the boundary slot.</summary>
    public ChainBlock First { get; }

    /// <summary>The epoch-2 blocks at slots 64, 65 and 66, each on the previous one (the first on <see cref="First"/>).</summary>
    public IReadOnlyList<ChainBlock> Voting { get; }

    /// <summary>The sole committee of slot 32, ascending: validators whose epoch-1 vote is for <see cref="First"/>.</summary>
    public ulong[] Committee32 { get; }

    /// <summary>How many of the slot-31 voters for the anchor cast no later epoch-1 vote, which would replace it as their latest message.</summary>
    public int LastFuluVotesStanding { get; }

    /// <summary>A runner rooted at the anchor, with this chain as both of its state providers unless <paramref name="withGloasStates"/> is false.</summary>
    /// <param name="pubkeys">The runner's pubkey cache; the anchor registry's when omitted.</param>
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

    /// <summary>
    /// The Fulu state of <see cref="CreateFuluState"/> made a genesis: real validator keys, finality at
    /// epoch 0, and a latest header that hashes to the returned anchor block once its state root is filled.
    /// </summary>
    private static BeaconStateFulu CreateAnchorState(out BeaconBlock anchorBlock)
    {
        BeaconStateFulu state = CreateFuluState(ValidatorCount);
        state.FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero };
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            validators[i] = updated;
        }

        BlsPublicKey[] syncCommittee = Enumerable.Repeat(validators[0].Pubkey, Presets.SyncCommitteeSize).ToArray();
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = syncCommittee, AggregatePubkey = Pubkey(0x60) };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = syncCommittee, AggregatePubkey = Pubkey(0x61) };

        anchorBlock = TestChain.CreateBlock(0, Hash256.Zero).Message!;
        state.LatestBlockHeader = new BeaconBlockHeader
        {
            Slot = 0,
            ProposerIndex = anchorBlock.ProposerIndex,
            ParentRoot = anchorBlock.ParentRoot,
            StateRoot = Hash256.Zero,
            BodyRoot = SszRoots.HashTreeRoot(anchorBlock.Body!),
        };
        anchorBlock.StateRoot = SszRoots.HashTreeRoot(state);
        return state;
    }

    /// <summary>Applies a self-built block carrying <paramref name="attestations"/> to the live <paramref name="state"/> and freezes a copy of the result under the block's root.</summary>
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
