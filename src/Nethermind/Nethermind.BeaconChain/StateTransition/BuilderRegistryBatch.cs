// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Indexes and updates a builder registry within one ordered request batch.</summary>
internal sealed class BuilderRegistryBatch(Builder[] original, ulong currentEpoch, HashSet<BlsPublicKey>? requestedPubkeys = null)
{
    private List<Builder>? _builders;
    private Dictionary<BlsPublicKey, int>? _indices;
    private BlsPublicKey? _firstPubkey;
    private int _firstIndex;
    private int _nextReusableIndex;
    private int Count => _builders?.Count ?? original.Length;

    public Builder this[int index]
    {
        get => _builders is null ? original[index] : _builders[index];
        set
        {
            (_builders ??= [.. original])[index] = value;
            if (value.Balance == 0 && value.WithdrawableEpoch <= currentEpoch)
                _nextReusableIndex = Math.Min(_nextReusableIndex, index);
        }
    }

    public int Find(BlsPublicKey pubkey)
    {
        if (_indices is null)
        {
            if (_firstPubkey is null)
            {
                _firstPubkey = pubkey;
                _firstIndex = -1;
                for (int i = 0; i < Count; i++)
                    if (this[i].Pubkey == pubkey) return _firstIndex = i;
                return -1;
            }
            if (_firstPubkey == pubkey) return _firstIndex;
            _indices = [with(requestedPubkeys?.Count ?? Count)];
            for (int i = 0; i < Count; i++)
            {
                BlsPublicKey key = this[i].Pubkey;
                if (requestedPubkeys is null || requestedPubkeys.Contains(key)) _indices.TryAdd(key, i);
            }
        }
        return _indices.GetValueOrDefault(pubkey, -1);
    }

    public void Add(Builder builder)
    {
        while (_nextReusableIndex < Count
            && (this[_nextReusableIndex].WithdrawableEpoch > currentEpoch || this[_nextReusableIndex].Balance != 0))
            _nextReusableIndex++;
        int index = _nextReusableIndex++;
        if (index < Count)
        {
            BlsPublicKey oldPubkey = this[index].Pubkey;
            _indices?.Remove(oldPubkey);
            if (_firstPubkey == oldPubkey) _firstIndex = -1;
            this[index] = builder;
        }
        else
        {
            (_builders ??= [.. original]).Add(builder);
        }
        _indices?.Add(builder.Pubkey, index);
        if (_firstPubkey == builder.Pubkey) _firstIndex = index;
    }

    public Builder[] ToArray() => _builders is null ? original : [.. _builders];
}
