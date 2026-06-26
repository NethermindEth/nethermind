// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
            .AddDecorator<IGenesisBuilder, AuRaGenesisBuilder>();
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

        // Reached only when the chainspec declares `authorityRound` but omits the genesis signature.
        // The zero signature gives this genesis a `step`+`signature` shape rather than master's
        // `mixHash`+`nonce`, so its hash differs from master for such a chainspec. No bundled AuRa
        // chain hits this — gnosis/chiado both ship the 65-byte signature — so it is effectively
        // unreachable; the fallback exists only to keep genesis a valid AuRa header.
        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(genesis.Header);
        upgraded.AuRaSignature = new byte[65];
        upgraded.Hash = new Hash256(upgraded.CalculateHash());
        return genesis.WithReplacedHeader(upgraded);
    }
}
