// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Authenticated ETH recovery links and envelopes retained with validated lean objects.</summary>
public sealed class LeanEthTransactions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<PublicKey, (object Owner, Func<ValueHash256[], Func<int, bool>, CancellationToken, Task<Transaction[]>> Recover)> _peers = [];
    private Func<ValueHash256, Transaction?>? _lookup;

    internal void SetLookup(Func<ValueHash256, Transaction?> lookup) => _lookup = lookup;

    internal void Register(PublicKey key, object owner, Func<ValueHash256[], Func<int, bool>, CancellationToken, Task<Transaction[]>> recover)
    {
        lock (_gate) _peers[key] = (owner, recover);
    }

    internal void Remove(PublicKey key, object owner)
    {
        lock (_gate)
            if (_peers.TryGetValue(key, out (object Owner, Func<ValueHash256[], Func<int, bool>, CancellationToken, Task<Transaction[]>> Recover) current) && ReferenceEquals(current.Owner, owner)) _peers.Remove(key);
    }

    internal bool HasPeer(PublicKey key)
    {
        lock (_gate) return _peers.ContainsKey(key);
    }

    internal Task<Transaction[]> RecoverAsync(PublicKey key, ValueHash256[] hashes, Func<int, bool> reserve, CancellationToken token)
    {
        Func<ValueHash256[], Func<int, bool>, CancellationToken, Task<Transaction[]>>? recover;
        lock (_gate) recover = _peers.TryGetValue(key, out (object Owner, Func<ValueHash256[], Func<int, bool>, CancellationToken, Task<Transaction[]>> Recover) peer) ? peer.Recover : null;
        return recover is null ? Task.FromResult(Array.Empty<Transaction>()) : recover(hashes, reserve, token);
    }

    internal bool TryGetRetained(in ValueHash256 hash, out Transaction? transaction)
    {
        transaction = _lookup?.Invoke(hash);
        return transaction is not null;
    }
}
