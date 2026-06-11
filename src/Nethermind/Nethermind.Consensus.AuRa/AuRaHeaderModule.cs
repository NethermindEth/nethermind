// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Registers AuRa header typing: replaces the <see cref="BlockHeader"/> RLP decoders globally so
/// every decode path (network, stores, static <see cref="Rlp"/>) materialises
/// <see cref="AuRaBlockHeader"/>, and decorates <see cref="IGenesisBuilder"/> so the genesis header
/// is AuRa-typed. Part of <see cref="AuRaModule"/>; also used by AuRa-flavoured test blockchains
/// that don't load the full plugin.
/// </summary>
public class AuRaHeaderModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        AuRaHeaderDecoder headerDecoder = new();
        BlockDecoder blockDecoder = new(headerDecoder);
        Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
        Rlp.RegisterDecoder(typeof(Block), blockDecoder);

        builder
            .AddSingleton<IHeaderDecoder>(headerDecoder)
            .AddSingleton(blockDecoder)
            .AddDecorator<IGenesisBuilder, AuRaGenesisBuilder>();
    }
}

/// <summary>
/// Ensures the genesis block carries an <see cref="AuRaBlockHeader"/>. No-op when the chainspec
/// already stamped the seal (see <c>AuRaChainSpecLoader</c>); otherwise upgrades the header with
/// the default step 0 + empty signature seal.
/// </summary>
public class AuRaGenesisBuilder(IGenesisBuilder inner) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = inner.Build();
        if (genesis.Header is AuRaBlockHeader) return genesis;

        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(genesis.Header);
        upgraded.AuRaSignature = new byte[65];
        upgraded.Hash = new Hash256(upgraded.CalculateHash());
        return genesis.WithReplacedHeader(upgraded);
    }
}
