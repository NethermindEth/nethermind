// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft;

/// <summary>Ensures the genesis block carries a <see cref="BftBlockHeader"/>; genesis hashes as-is so its hash is unchanged.</summary>
public class BftGenesisBuilder(IGenesisBuilder inner, IBftExtraDataCodecSelector codecs) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = inner.Build();
        if (genesis.Header is BftBlockHeader) return genesis;

        BftBlockHeader upgraded = BftBlockHeader.UpgradeFrom(genesis.Header, codecs.ForBlock(genesis.Number));
        upgraded.Hash = new Hash256(upgraded.CalculateHash());
        return genesis.WithReplacedHeader(upgraded);
    }
}
