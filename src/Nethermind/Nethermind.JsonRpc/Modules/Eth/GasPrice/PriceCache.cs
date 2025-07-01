// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice;

public struct PriceCache
{
    public PriceCache(Hash256? headHash, UInt256? price)
    {
        LastHeadHash = headHash;
        LastPrice = price;
    }

    public UInt256? LastPrice { get; private set; }
    private Hash256? LastHeadHash { get; set; }

    public void Set(Hash256 headHash, UInt256 price)
    {
        LastHeadHash = headHash;
        LastPrice = price;
    }

    public readonly bool TryGetPrice(Hash256 headHash, out UInt256? price)
    {
        if (headHash == LastHeadHash)
        {
            price = LastPrice;
            return true;
        }
        else
        {
            price = null;
            return false;
        }
    }
}
