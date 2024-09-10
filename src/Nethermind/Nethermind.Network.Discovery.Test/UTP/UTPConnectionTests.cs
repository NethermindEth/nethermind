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

        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            return _implementation!.ReceiveMessage(meta, data, token);
        }
    }

    internal class ValidateST_SYNPacket : IUTPTransfer
    {

        public Task ReceiveMessage(UTPPacketHeader header, ReadOnlySpan<byte> data, CancellationToken token)
        {
            Assert.NotNull(header);
            Assert.NotNull(header.SeqNumber);
            Assert.NotNull(header.ConnectionId);
            Assert.NotNull(header.PacketType);
            Assert.That(header.Version, Is.EqualTo(1));
            Assert.That(header.SeqNumber, Is.EqualTo(1));
            Assert.That(header.PacketType, Is.EqualTo(UTPPacketType.StSyn));
            return Task.CompletedTask;
        }
    }

    internal class ValidateST_STATE_Packet(ushort connectionId) : IUTPTransfer
    {

        public Task ReceiveMessage(UTPPacketHeader header, ReadOnlySpan<byte> data, CancellationToken token)
        {
            Assert.NotNull(header);
            Assert.NotNull(header.SeqNumber);
            Assert.NotNull(header.AckNumber);
            Assert.NotNull(header.ConnectionId);
            Assert.NotNull(header.PacketType);
            Assert.That(header.Version, Is.EqualTo(1));
            Assert.That(header.PacketType, Is.EqualTo(UTPPacketType.StState));
            Assert.That(header.ConnectionId, Is.EqualTo(connectionId));
            //Assert.That(header.AckNumber, Is.EqualTo(connectionId));

            return Task.CompletedTask;
        }
    }
}
