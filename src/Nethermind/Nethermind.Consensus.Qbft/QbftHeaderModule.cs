// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft;

/// <summary>
/// Registers QBFT header typing: the <see cref="BlockHeader"/> RLP decoders (globally and in DI) and
/// the <see cref="IGenesisBuilder"/> decorator that types the genesis header.
/// </summary>
public class QbftHeaderModule(IBftExtraDataCodecSelector codecs) : Module
{
    public QbftHeaderModule() : this(QbftOnlyCodecSelector.Instance) { }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        QbftHeaderDecoder headerDecoder = new(codecs);
        BlockDecoder blockDecoder = new(headerDecoder);
        BlockBodyDecoder blockBodyDecoder = new(headerDecoder);
        Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
        Rlp.RegisterDecoder(typeof(Block), blockDecoder);
        Rlp.RegisterDecoder(typeof(BlockBody), blockBodyDecoder);

        builder
            .AddSingleton<IBftExtraDataCodecSelector>(codecs)
            .AddSingleton<IHeaderDecoder>(headerDecoder)
            .AddSingleton(blockDecoder)
            .AddSingleton(blockBodyDecoder)
            .AddDecorator<IGenesisBuilder, QbftGenesisBuilder>();
    }
}

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
