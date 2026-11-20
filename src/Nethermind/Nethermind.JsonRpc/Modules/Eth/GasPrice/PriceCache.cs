// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice;

/// <remarks>
/// The oracle shares one cache between concurrent requests, so the head hash and its price are published together as one
/// immutable entry: a reader gets the hash with the price stored for it, never a new hash with the previous price.
/// </remarks>
public struct PriceCache(Hash256? headHash, UInt256? price)
{
    private Entry? _entry = new(headHash, price);

    public readonly UInt256? LastPrice => Volatile.Read(in _entry)?.Price;

    public void Set(Hash256 headHash, UInt256 price) => Volatile.Write(ref _entry, new Entry(headHash, price));

    public readonly bool TryGetPrice(Hash256 headHash, out UInt256? price)
    {
        Entry? entry = Volatile.Read(in _entry);
        if (entry is not null && headHash == entry.HeadHash)
        {
            price = entry.Price;
            return true;
        }

        price = null;
        return false;
    }

    private sealed class Entry(Hash256? headHash, UInt256? price)
    {
        public readonly Hash256? HeadHash = headHash;
        public readonly UInt256? Price = price;
    }
}
