// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Legacy;

/// <summary>Runs one state lineage from any fork before Gloas, upgrading it at each activation epoch.</summary>
/// <remarks>From Fulu on, the current state transition is used. Not thread-safe.</remarks>
internal sealed class LegacyChain
{
    private readonly BeaconChainSpec _spec;
    private readonly PubkeyCache _pubkeys;
    private readonly INewPayloadNotifier _notifier;
    private readonly bool _verifySignatures;
    private readonly Phase0.Transition[] _transitions;
    private EpochCache _cache;

    /// <param name="stateSsz">A state of the fork its slot belongs to.</param>
    public LegacyChain(BeaconChainSpec spec, ReadOnlySpan<byte> stateSsz, INewPayloadNotifier notifier, bool verifySignatures)
    {
        _spec = spec;
        _notifier = notifier;
        _verifySignatures = verifySignatures;
        _transitions =
        [
            new Phase0.Transition(spec),
            new Altair.Transition(spec),
            new Bellatrix.Transition(spec),
            new Capella.Transition(spec),
            new Deneb.Transition(spec),
            new Electra.Transition(spec),
        ];

        Phase0.Transition? transition = At(BeaconStateCodec.ReadSlot(stateSsz));
        if (transition is null)
        {
            BeaconStateFulu.Decode(stateSsz, out BeaconStateFulu state);
            State = state;
        }
        else
        {
            State = transition.DecodeState(stateSsz);
        }
        _cache = NewCache(transition is null ? new CachedBeaconStateHasher() : new LegacyStateHasher(transition.Fork));
        _pubkeys = new PubkeyCache();
        if (verifySignatures)
            _pubkeys.Build(State.ValidatorBuffer!.Items);
    }

    public BeaconStateFulu State { get; }

    public Hash256 ComputeStateRoot() => _cache.Hasher.HashTreeRoot(State);

    /// <summary>Spec <c>state_transition</c> of a serialized signed block.</summary>
    /// <remarks>
    /// The state at the end of an era contains the block at its slot, which the file of the next era repeats, as the first
    /// file does the unsigned genesis block. Such a block is skipped; another one in its place fails as the next block's parent.
    /// </remarks>
    /// <exception cref="BeaconStateException">The block is invalid; the lineage must be discarded.</exception>
    public void ApplyBlock(ReadOnlySpan<byte> ssz)
    {
        if (!SignedBeaconBlockCodec.TryReadSlot(ssz, out ulong slot))
            throw new BeaconStateException("Signed beacon block SSZ does not carry a slot");
        if (slot == State.Slot && slot == State.LatestBlockHeader!.Slot)
            return;
        AdvanceTo(slot);

        if (At(slot) is { } transition)
        {
            transition.Apply(State, transition.DecodeBlock(ssz), _cache, _pubkeys, _notifier, _verifySignatures);
            return;
        }
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        StateTransition.StateTransition.ApplyPrepared(State, block, _cache, _pubkeys, _notifier, _spec, validateResult: true, _verifySignatures);
    }

    /// <summary>Spec <c>process_slots</c>, upgrading the state at every fork boundary up to and including <paramref name="targetSlot"/>.</summary>
    public void AdvanceTo(ulong targetSlot)
    {
        if (_spec.GetEpoch(targetSlot) >= _spec.GloasForkEpoch)
            throw new NotSupportedException($"Slot {targetSlot} is past the Gloas fork, which cannot be replayed from beacon blocks alone");

        while (State.Slot < targetSlot)
        {
            Phase0.Transition? transition = At(State.Slot);
            ulong boundary = NextForkSlot(transition);
            ulong epochStart = BeaconStateAccessors.ComputeStartSlotAtEpoch(State.GetCurrentEpoch() + 1);
            // One epoch at a time, so that the keys of validators appended meanwhile are cached before they are used.
            ulong next = Math.Min(targetSlot, epochStart);
            if (transition is null)
                SlotProcessing.ProcessSlots(State, next, _cache);
            else
                transition.ProcessSlots(State, next, _cache);

            if (State.Slot == boundary)
                Upgrade(transition!);
            if (State.Slot == epochStart && _verifySignatures)
                _pubkeys.FinalizeRegistry(State.ValidatorBuffer!.Items);
        }
    }

    private void Upgrade(Phase0.Transition from)
    {
        int next = (int)from.Fork + 1;
        if (next < _transitions.Length)
        {
            _transitions[next].Upgrade(State, _cache);
            ((LegacyStateHasher)_cache.Hasher).Fork = (LegacyFork)next;
        }
        else
        {
            ((Electra.Transition)from).UpgradeToFulu(State);
            _cache = NewCache(new CachedBeaconStateHasher());
        }
    }

    /// <summary>The transition of the fork at <paramref name="slot"/>, or <c>null</c> from Fulu on.</summary>
    private Phase0.Transition? At(ulong slot)
    {
        ulong epoch = _spec.GetEpoch(slot);
        if (epoch >= _spec.FuluForkEpoch)
            return null;
        for (int i = _transitions.Length - 1; i > 0; i--)
        {
            if (epoch >= _spec.Forks[i].Epoch)
                return _transitions[i];
        }
        return _transitions[0];
    }

    private ulong NextForkSlot(Phase0.Transition? transition)
    {
        if (transition is null)
            return ulong.MaxValue;
        int next = (int)transition.Fork + 1;
        ulong epoch = next < _transitions.Length ? _spec.Forks[next].Epoch : _spec.FuluForkEpoch;
        return epoch > ulong.MaxValue / _spec.SlotsPerEpoch ? ulong.MaxValue : BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
    }

    private EpochCache NewCache(IBeaconStateHasher hasher) => new()
    {
        Hasher = hasher,
        SigningSpec = _spec,
        RequestRegistry = new ValidatorPubkeyIndex(),
    };
}
