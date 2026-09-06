// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Test;
using Nethermind.Xdc.Discovery;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Discovery;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcDiscoveryTests
{
    [Test]
    public void XdcPingMsgSerializer_WritesXdcTypeByte()
    {
        // Packet layout: 32-byte MDC + 64-byte sig + 1-byte recovery id + 1-byte type = type at index 97
        Ecdsa ecdsa = new();
        XdcPingMsgSerializer serializer = new(ecdsa, new SameKeyGenerator(TestItem.PrivateKeyA), new NodeIdResolver(ecdsa));
        PingMsg msg = new(
            TestItem.PublicKeyA,
            Timestamper.Default.UnixTime.SecondsLong + 1200,
            new(IPAddress.Loopback, 30303),
            new(IPAddress.Loopback, 30304),
            new byte[32]
            );

        using DisposableByteBuffer buffer = Unpooled.Buffer().AsDisposable();
        serializer.Serialize(buffer, msg);
        Assert.That(buffer.GetByte(97), Is.EqualTo((byte)5));
    }

    private sealed class ExposedXdcNettyDiscoveryHandler : XdcNettyDiscoveryHandler
    {
        public ExposedXdcNettyDiscoveryHandler()
            : base(
                Substitute.For<IDiscoveryMsgListener>(),
                Substitute.For<DotNetty.Transport.Channels.IChannel>(),
                Substitute.For<IMessageSerializationService>(),
                Timestamper.Default,
                Nethermind.Logging.LimboLogs.Instance)
        {
        }

        public MsgType? ExposedFromMsgTypeByte(byte b) => FromMsgTypeByte(b);
    }

    [TestCase((byte)5, MsgType.Ping)]
    [TestCase((byte)1, null)]
    public void XdcNettyDiscoveryHandler_FromMsgTypeByte(byte input, MsgType? expected)
    {
        ExposedXdcNettyDiscoveryHandler handler = new();
        Assert.That(handler.ExposedFromMsgTypeByte(input), Is.EqualTo(expected));
    }
}
