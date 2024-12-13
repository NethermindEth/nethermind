using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Discovery.UTP;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Tests;

public class UTPConnectionTests
{

    [Test]
    public void testSendingST_SYN_Packet()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var sender = new UTPStream(new ValidateST_SYNPacket(), 0, LimboLogs.Instance);

        Assert.ThrowsAsync<OperationCanceledException>(async () => await sender.InitiateHandshake(cts.Token));
    }

    [Test]
    public async Task testAnsweringST_STATE_Packet()
    {
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var receiverPeer = new ReceiverPeer();
        var sender = new UTPStream(receiverPeer, 0, LimboLogs.Instance);
        var receiver = new UTPStream(new ValidateST_STATE_Packet(0), 0, LimboLogs.Instance);
        receiverPeer._implementation = receiver;

        MemoryStream output = new MemoryStream();
        CancellationToken token = cts.Token;
        Task senderTask = Task.Run(() =>
        {
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sender.InitiateHandshake(token));

        }, cts.Token);


        Task receiverTask = Task.Run(() =>
        {
            Assert.ThrowsAsync<OperationCanceledException>(async () => await receiver.ReadStream(output, token));

        }, cts.Token);
        await Task.WhenAll(senderTask, receiverTask);
    }

    internal class ReceiverPeer : IUTPTransfer
    {
        internal IUTPTransfer? _implementation;

        public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
        {
            return _implementation!.ReceiveMessage(packetHeader, data, token);
        }
    }

    internal class ValidateST_SYNPacket : IUTPTransfer
    {

        public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
        {
            Assert.That(packetHeader, Is.Not.Null);
            Assert.That(packetHeader.SeqNumber, Is.Not.Default);
            Assert.That(packetHeader.ConnectionId, Is.Not.Default);
            Assert.That(packetHeader.PacketType, Is.Not.Default);
            Assert.That(packetHeader.Version, Is.EqualTo(1));
            Assert.That(packetHeader.SeqNumber, Is.EqualTo(1));
            Assert.That(packetHeader.PacketType, Is.EqualTo(UTPPacketType.StSyn));
            return Task.CompletedTask;
        }
    }

    internal class ValidateST_STATE_Packet(ushort connectionId) : IUTPTransfer
    {

        public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
        {
            Assert.That(packetHeader, Is.Not.Null);
            Assert.That(packetHeader.SeqNumber, Is.Not.Default);
            Assert.That(packetHeader.AckNumber, Is.Not.Default);
            Assert.That(packetHeader.ConnectionId, Is.Not.Default);
            Assert.That(packetHeader.PacketType, Is.Not.Default);
            Assert.That(packetHeader.Version, Is.EqualTo(1));
            Assert.That(packetHeader.PacketType, Is.EqualTo(UTPPacketType.StState));
            Assert.That(packetHeader.ConnectionId, Is.EqualTo(connectionId));
            //Assert.That(header.AckNumber, Is.EqualTo(connectionId));

            return Task.CompletedTask;
        }
    }
}
