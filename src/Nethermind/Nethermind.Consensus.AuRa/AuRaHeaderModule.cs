// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Registers AuRa header typing: the <see cref="BlockHeader"/> RLP decoders (globally and in DI)
/// and the <see cref="IGenesisBuilder"/> decorator. Part of <see cref="AuRaModule"/>; also used by
/// AuRa-flavoured test blockchains that don't load the full plugin.
/// </summary>
public class AuRaHeaderModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        AuRaHeaderDecoder headerDecoder = new();
        BlockDecoder blockDecoder = new(headerDecoder);
        BlockBodyDecoder blockBodyDecoder = new(headerDecoder);
        Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
        Rlp.RegisterDecoder(typeof(Block), blockDecoder);
        Rlp.RegisterDecoder(typeof(BlockBody), blockBodyDecoder);

        builder
            .AddSingleton<IHeaderDecoder>(headerDecoder)
            .AddSingleton(blockDecoder)
            .AddSingleton(blockBodyDecoder)
            .AddDecorator<IGenesisBuilder, AuRaGenesisBuilder>()
            .AddSingleton<IBlockForRpcFactory, AuRaBlockForRpcFactory>();
    }
}

/// <summary>
/// Ensures the genesis block carries an <see cref="AuRaBlockHeader"/>; no-op when the chainspec
/// already stamped the seal (see <c>AuRaChainSpecLoader</c>).
/// </summary>
public class AuRaGenesisBuilder(IGenesisBuilder inner) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = inner.Build();
        if (genesis.Header is AuRaBlockHeader) return genesis;

        // Reached only if a chainspec declares `authorityRound` but omits the genesis signature
        // (no bundled chain does). The zero signature shifts genesis to a step+signature shape,
        // diverging from master's mixHash+nonce hash for that chainspec.
        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(genesis.Header);
        upgraded.AuRaSignature = new byte[65];
        upgraded.Hash = new Hash256(upgraded.CalculateHash());
        return genesis.WithReplacedHeader(upgraded);
    }
}
