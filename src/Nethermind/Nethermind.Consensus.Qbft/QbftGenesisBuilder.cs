// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft;

/// <summary>Ensures the genesis block carries a <see cref="QbftBlockHeader"/>; genesis hashes as-is so its hash is unchanged.</summary>
public class QbftGenesisBuilder(IGenesisBuilder inner, IBftExtraDataCodecSelector codecs) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = inner.Build();
        if (genesis.Header is QbftBlockHeader) return genesis;

        QbftBlockHeader upgraded = QbftBlockHeader.UpgradeFrom(genesis.Header, codecs.ForBlock(genesis.Number));
        upgraded.Hash = new Hash256(upgraded.CalculateHash());
        return genesis.WithReplacedHeader(upgraded);
    }
}
