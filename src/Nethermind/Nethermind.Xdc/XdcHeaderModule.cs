// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

/// <summary>
/// Registers XDC header typing: the <see cref="BlockHeader"/> RLP decoders (globally and in DI)
/// so that call sites which resolve decoders from the static <see cref="Rlp"/> registry instead of
/// DI (e.g. <c>ProofRpcModule</c>) also encode/decode XDC headers correctly. Used by
/// <see cref="XdcModule"/> for mainnet headers and by <see cref="XdcSubnetModule"/> with a
/// subnet-specific decoder instance.
/// </summary>
/// <remarks>
/// <see cref="BaseXdcHeaderDecoder{TH}"/> falls back to the base Ethereum shape when asked to encode
/// a header that isn't its own subtype, mirroring <c>AuRaHeaderDecoder</c>'s seal-only fallback — this
/// is what makes it safe to register as the process-wide default.
/// </remarks>
public class XdcHeaderModule(IHeaderDecoder headerDecoder) : Module
{
    public XdcHeaderModule() : this(new XdcHeaderDecoder()) { }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        BlockDecoder blockDecoder = new(headerDecoder);
        BlockBodyDecoder blockBodyDecoder = new(headerDecoder);
        Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
        Rlp.RegisterDecoder(typeof(Block), blockDecoder);
        Rlp.RegisterDecoder(typeof(BlockBody), blockBodyDecoder);

        builder
            .AddSingleton<IHeaderDecoder>(headerDecoder)
            .AddSingleton(blockDecoder)
            .AddSingleton(blockBodyDecoder);
    }
}
