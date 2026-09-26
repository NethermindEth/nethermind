// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.TxPool;

internal sealed class DelegationCache(ulong chainId)
{
    // Keyed by authority for a lock-free miss; the nonce lists are read and written only under _lock.
    private readonly ConcurrentDictionary<AddressAsKey, List<ulong>> _pendingNonces = new();
    private readonly Lock _lock = new();

    /// <summary>Returns the authority <paramref name="tuple"/> can delegate on this chain, or <c>null</c> when it cannot.</summary>
    /// <remarks>EIP-7702 skips a tuple whose chain id is neither 0 nor the local one, so such a tuple never delegates its authority.</remarks>
    public Address? GetAuthority(AuthorizationTuple tuple) =>
        tuple.ChainId.IsZero || tuple.ChainId == chainId ? tuple.Authority : null;

    /// <summary>Whether a pooled authorization by <paramref name="authority"/> can still apply at or after <paramref name="accountNonce"/>.</summary>
    /// <remarks>An authorization applies only while its nonce equals the authority's, and account nonces never decrease,
    /// so one below <paramref name="accountNonce"/> can never apply again.</remarks>
    public bool HasPending(AddressAsKey authority, ulong accountNonce)
    {
        if (!_pendingNonces.TryGetValue(authority, out List<ulong>? nonces))
            return false;

        lock (_lock)
        {
            foreach (ulong nonce in nonces)
            {
                if (nonce >= accountNonce)
                    return true;
            }
        }

        return false;
    }

    public void Add(AuthorizationTuple tuple)
    {
        if (GetAuthority(tuple) is not { } authority)
            return;

        lock (_lock)
        {
            _pendingNonces.GetOrAdd(authority, static _ => []).Add(tuple.Nonce);
        }
    }

    public void Remove(AuthorizationTuple tuple)
    {
        if (GetAuthority(tuple) is not { } authority)
            return;

        lock (_lock)
        {
            if (_pendingNonces.TryGetValue(authority, out List<ulong>? nonces) && nonces.Remove(tuple.Nonce) && nonces.Count == 0)
                _pendingNonces.TryRemove(authority, out _);
        }
    }
}
