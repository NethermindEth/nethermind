// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Verifies that <see cref="XdcHeaderModule"/> wires the correct <see cref="BlockHeader"/> decoders,
/// both in DI and as the process-wide static <see cref="Rlp"/> default (mirrors
/// <c>Nethermind.AuRa.Test.Encoding.AuRaHeaderDecoderTests</c>).
/// </summary>
[TestFixture, NonParallelizable]
public class XdcHeaderModuleTests
{
    [TearDown]
    public void TearDown() => Rlp.ResetDecoders();

    [Test]
    public void Default_constructor_registers_XdcHeaderDecoder()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new XdcHeaderModule())
            .Build();

        Assert.That(container.Resolve<IHeaderDecoder>(), Is.InstanceOf<XdcHeaderDecoder>());
        Assert.That(container.Resolve<BlockDecoder>(), Is.Not.Null);
        Assert.That(container.Resolve<BlockBodyDecoder>(), Is.Not.Null);
    }

    [Test]
    public void Provided_decoder_instance_is_used_for_subnet_chains()
    {
        XdcSubnetHeaderDecoder subnetDecoder = new();

        using IContainer container = new ContainerBuilder()
            .AddModule(new XdcHeaderModule(subnetDecoder))
            .Build();

        Assert.That(container.Resolve<IHeaderDecoder>(), Is.SameAs(subnetDecoder));
    }

    [Test]
    public void Load_registers_decoder_globally_so_static_Rlp_calls_use_it()
    {
        new ContainerBuilder()
            .AddModule(new XdcHeaderModule())
            .Build();

        Assert.That(Rlp.GetDecoder<BlockHeader>(), Is.InstanceOf<XdcHeaderDecoder>());
        Assert.That(Rlp.GetDecoder<Block>(), Is.Not.Null);
        Assert.That(Rlp.GetDecoder<BlockBody>(), Is.Not.Null);
    }

    /// <summary>
    /// Regression guard: once XDC's decoder is the global default, code that still encodes a plain
    /// (non-XDC) <see cref="BlockHeader"/> — e.g. test fixtures shared with generic-chain tests —
    /// must not start throwing.
    /// </summary>
    [Test]
    public void Global_decoder_still_encodes_plain_BlockHeader()
    {
        new ContainerBuilder()
            .AddModule(new XdcHeaderModule())
            .Build();

        BlockHeader plain = Build.A.BlockHeader.TestObject;

        Rlp encoded = Rlp.Encode(plain);

        Assert.That(encoded.Bytes, Is.Not.Empty);
    }
}
