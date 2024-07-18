using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.Discovery.UTP;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Tests;

public class UTPTests
{

    // ChatGPT generated
    private static byte[] HexStringToByteArray(string hex)
    {
        return Enumerable.Range(0, hex.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
            .ToArray();
    }

    public static IEnumerable<(UTPPacketHeader header, byte[] payload, byte[] encoding)> EncodingTestCases()
    {
        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StSyn,
            Version = 1,
            ConnectionId = 10049,
            TimestampMicros = 3384187322,
            TimestampDeltaMicros = 0,
            WindowSize = 1048576,
            SeqNumber = 11884,
            AckNumber = 0
        }, Array.Empty<byte>(), HexStringToByteArray("41002741c9b699ba00000000001000002e6c0000"));

        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StState,
            Version = 1,
            ConnectionId = 10049,
            TimestampMicros = 6195294,
            TimestampDeltaMicros = 916973699,
            WindowSize = 1048576,
            SeqNumber = 16807,
            AckNumber = 11885
        }, Array.Empty<byte>(), HexStringToByteArray("21002741005e885e36a7e8830010000041a72e6d"));


        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StState,
            Version = 1,
            ConnectionId = 10049,
            TimestampMicros = 6195294,
            TimestampDeltaMicros = 916973699,
            WindowSize = 1048576,
            SeqNumber = 16807,
            AckNumber = 11885,

            SelectiveAck = [1, 0, 0, 128],
        }, Array.Empty<byte>(), HexStringToByteArray("21012741005e885e36a7e8830010000041a72e6d000401000080"));

        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StData,
            Version = 1,
            ConnectionId = 26237,
            TimestampMicros = 252492495,
            TimestampDeltaMicros = 242289855,
            WindowSize = 1048576,
            SeqNumber = 8334,
            AckNumber = 16806
        }, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9], HexStringToByteArray("0100667d0f0cbacf0e710cbf00100000208e41a600010203040506070809"));


        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StFin,
            Version = 1,
            ConnectionId = 19003,
            TimestampMicros = 515227279,
            TimestampDeltaMicros = 511481041,
            WindowSize = 1048576,
            SeqNumber = 41050,
            AckNumber = 16806
        }, [], HexStringToByteArray("11004a3b1eb5be8f1e7c94d100100000a05a41a6"));


        yield return (new UTPPacketHeader
        {
            PacketType = UTPPacketType.StReset,
            Version = 1,
            ConnectionId = 62285,
            TimestampMicros = 751226811,
            TimestampDeltaMicros = 0,
            WindowSize = 0,
            SeqNumber = 55413,
            AckNumber = 16807
        }, [], HexStringToByteArray("3100f34d2cc6cfbb0000000000000000d87541a7"));

    }


    [TestCaseSource(nameof(EncodingTestCases))]
    public void TestDecode((UTPPacketHeader header, byte[] payload, byte[] encoding) testCase)
    {
        (UTPPacketHeader header, byte[] payload, byte[] encoding) = testCase;
        (UTPPacketHeader decodedHeader, int readLength) = UTPPacketHeader.DecodePacket(encoding);

        Assert.That(decodedHeader, Is.EqualTo(header));
        Assert.That(encoding[readLength..], Is.EqualTo(payload));
    }

    [TestCaseSource(nameof(EncodingTestCases))]
    public void TestEncode((UTPPacketHeader header, byte[] payload, byte[] encoding) testCase)
    {
        (UTPPacketHeader header, byte[] payload, byte[] encoding) = testCase;
        Span<byte> buffer = stackalloc byte[520];

        ReadOnlySpan<byte> encodePacket = UTPPacketHeader.EncodePacket(header, payload, buffer);

        Assert.That(encoding, Is.EqualTo(encodePacket.ToArray()));
    }

    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", new ushort[0])]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", new ushort[] { 2 })]
    [TestCase("0000000100000000000000000000000000000000000000000000000000000000", new ushort[] { 2, 3 })]
    [TestCase("0000111100000000000000000000000000000000000000000000000000000000", new ushort[] { 2, 3, 4, 5, 6 })]
    [TestCase("0000000100000001000000010000000100000001000000000000000000000000", new ushort[] { 2, 3, 0, 11, 19, 27, 35 })]
    [TestCase("1000000000000000000000000000000000000000000000000000000000000000", new ushort[] { 10 })]
    [TestCase("1000000010000000001000000010000000100000001000000010000000100000", new ushort[] { 10, 18, 24, 32, 40, 48, 56, 64 })]
    public void TestCompileSelectiveAck(string stringRep, ushort[] pendingSequenceNums)
    {
        ConcurrentDictionary<ushort, Memory<byte>?> pendingSequence = new ConcurrentDictionary<ushort, Memory<byte>?>();
        foreach (var pendingSequenceNum in pendingSequenceNums)
        {
            pendingSequence[pendingSequenceNum] = Memory<byte>.Empty;
        }

        byte[] ackBitset = UTPStream.CompileSelectiveAckBitset(1, pendingSequence);

        string bitSetString = string.Concat(ackBitset.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        Assert.That(bitSetString, Is.EqualTo(stringRep));
    }

    public static IEnumerable<(string, Func<IUTPTransfer, IUTPTransfer>)> TransferMutators()
    {
        yield return ("no proxy", (t) => t);
        yield return ("noop proxy", (t) => new Randomizer(t, 0.00, 0, 0));
        yield return ("drop 5%", (t) => new Randomizer(t, 0.05, 0, 0));
        // yield return ("random delay 10ms", (t) => new Randomizer(t, 0.0, 10, 0));
        // yield return ("random delay 10ms and drop 5%", (t) => new Randomizer(t, 0.05, 10, 0));
        // yield return ("drop 5% 2", (t) => new Randomizer(t, 0.05, 0, 1));
        // yield return ("random delay 10ms 2", (t) => new Randomizer(t, 0.0, 10, 1));
        // yield return ("random delay 10ms and drop 5% 2", (t) => new Randomizer(t, 0.05, 10, 1));
    }


    [TestCaseSource(nameof(TransferMutators))]
    public async Task TestSend((string, Func<IUTPTransfer, IUTPTransfer>) test)
    {
        (string _, Func<IUTPTransfer, IUTPTransfer> transferMutator) = test;

        byte[] data = new byte[250000];
        new Random(0).NextBytes(data);

        MemoryStream input = new MemoryStream(data);
        MemoryStream output = new MemoryStream();

        PeerProxy proxy = new PeerProxy();
        UTPStream sender = new UTPStream(transferMutator.Invoke(proxy), 0);
        UTPStream receiver = new UTPStream(transferMutator.Invoke(sender), 0);
        proxy._implementation = receiver;

        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = cts.Token;

        Task senderTask = Task.Run(async () =>
        {
            try
            {
            await sender.InitiateHandshake(token);
            await sender.WriteStream(input, token);
            }
            catch (Exception e)
            {
             Console.Error.WriteLine($"Err {e}");
             throw;
            }
        }, token);

        Task receiverTask = Task.Run(async () =>
        {
            //await receiver.HandleReceiveHandshake(token);
            await receiver.ReadStream(output, token);
        }, token);

        await Task.WhenAll(senderTask, receiverTask);

        Assert.That(data, Is.EqualTo(output.ToArray()));
    }

    internal class PeerProxy : IUTPTransfer
    {
        internal IUTPTransfer? _implementation;

        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            return _implementation!.ReceiveMessage(meta, data, token);
        }
    }

    internal class Randomizer(IUTPTransfer actual, double dropPercentage, int randomDelayMs, int seed) : IUTPTransfer
    {
        private Random _random = new Random(seed);

        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            byte[] dataArr = data.ToArray();

            // Dont block
            _ = Task.Run(async () =>
            {
                // skipped
                if (_random.NextDouble() < dropPercentage)
                {
                    Console.Error.WriteLine($"Drop {meta}");
                    return;
                }

                if (randomDelayMs != 0)
                    await Task.Delay(_random.Next() % randomDelayMs, token);

                Console.Error.WriteLine($"Send {meta}");
                await actual.ReceiveMessage(meta, dataArr, token);
            }, token);

            return Task.CompletedTask;
        }
    }
}
