// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.TxPool;
internal sealed class DelegationCache
{
    private readonly ConcurrentDictionary<UInt256, int> _pendingDelegations = new();

    public bool HasPending(AddressAsKey key, UInt256 nonce)
    {
        return _pendingDelegations.ContainsKey(KeyMask(key, nonce));
    }

    public void DecrementDelegationCount(AddressAsKey key, UInt256 nonce)
    {
        InternalIncrement(key, nonce, false);
    }
    public void IncrementDelegationCount(AddressAsKey key, UInt256 nonce)
    {
        InternalIncrement(key, nonce, true);
    }

    private void InternalIncrement(AddressAsKey key, UInt256 nonce, bool increment)
    {
        UInt256 addressPlusNonce = KeyMask(key, nonce);

        int value = increment ? 1 : -1;
        var lastCount = _pendingDelegations.AddOrUpdate(addressPlusNonce,
            (k) =>
            {
                if (increment)
                    return 1;
                return 0;
            },
            (k, c) => c + value);

        if (lastCount == 0)
        {
            //Remove() is threadsafe and only removes if the count is the same as the updated one
            ((ICollection<KeyValuePair<UInt256, int>>)_pendingDelegations).Remove(
                new KeyValuePair<UInt256, int>(addressPlusNonce, lastCount));
        }
    }

    private static UInt256 KeyMask(AddressAsKey key, UInt256 nonce)
    {
        //A nonce cannot exceed 2^64-1 and an address is 20 bytes, so we can pack them together in one u256
        ref byte baseRef = ref key.Value.Bytes[0];
        return new UInt256(Unsafe.ReadUnaligned<ulong>(ref baseRef),
          Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref baseRef, 8)),
          Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref baseRef, 16)),
          nonce.u1);
    }
}
