// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Indexes the append-only validator pubkey registry along one admitted block lineage.</summary>
internal sealed class ValidatorPubkeyIndex
{
    // Chains start at the newest matching bucket entry. Zero ends the chain, so indices are stored plus one.
    private int[] _buckets = [];
    private int[] _next = [];
    private int _indexed;
    private Hash256? _tip;
    private Hash256? _parent;
    private bool _used;

    public void BeginBlock(Hash256 parent)
    {
        _parent = parent;
        _used = false;
    }

    public void AdmitBlock(Hash256 root)
    {
        if (_used || _tip == _parent)
            _tip = root;
    }

    public void Extend(Validator[] validators)
    {
        if (!_used && _parent != _tip)
        {
            _buckets = [];
            _next = [];
            _indexed = 0;
            _tip = null;
        }
        _used = true;
        if (_indexed == validators.Length)
            return;

        // A speculative append changes buckets. Only admission certifies them for a later block.
        _tip = null;
        if (validators.Length > _next.Length)
        {
            int capacity = checked((int)BitOperations.RoundUpToPowerOf2((uint)validators.Length));
            _next = new int[capacity];
            _buckets = new int[Math.Max(1, capacity / 2)];
            _indexed = 0;
        }

        for (; _indexed < validators.Length; _indexed++)
        {
            int bucket = validators[_indexed].Pubkey.GetHashCode() & (_buckets.Length - 1);
            _next[_indexed] = _buckets[bucket];
            _buckets[bucket] = _indexed + 1;
        }
    }

    public int Find(Validator[] validators, BlsPublicKey pubkey)
    {
        if (_indexed == 0)
            return -1;
        int bucket = pubkey.GetHashCode() & (_buckets.Length - 1);
        int first = -1;
        for (int entry = _buckets[bucket]; entry != 0; entry = _next[entry - 1])
        {
            if (validators[entry - 1].Pubkey == pubkey)
                first = entry - 1;
        }
        return first;
    }
}
