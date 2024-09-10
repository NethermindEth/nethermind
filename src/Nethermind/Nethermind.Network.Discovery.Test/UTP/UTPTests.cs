using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Discovery.UTP;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.UTP;

public class UTPTests
{
    internal static TestLogManager logManager = new TestLogManager(LogLevel.Trace);

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
        NonBlocking.ConcurrentDictionary<ushort, Memory<byte>?> pendingSequence = new NonBlocking.ConcurrentDictionary<ushort, Memory<byte>?>();
        foreach (var pendingSequenceNum in pendingSequenceNums)
        {
            pendingSequence[pendingSequenceNum] = Memory<byte>.Empty;
        }

        byte[] ackBitset = UTPUtil.CompileSelectiveAckBitset(1, pendingSequence)!;

        string bitSetString = string.Concat(ackBitset.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        Assert.That(bitSetString, Is.EqualTo(stringRep));
    }

    public static IEnumerable<(string, Func<IUTPTransfer, IUTPTransfer>)> TransferMutators()
    {
        yield return ("no proxy", (t) => t);
        yield return ("noop proxy", (t) => new Randomizer(t, 0.00, 0, 0, 0));
        yield return ("drop 5%", (t) => new Randomizer(t, 0.05, 0, 0, 0));
        yield return ("random delay 10ms", (t) => new Randomizer(t, 0.0, 10, 3, 0));
        yield return ("random delay 10ms and drop 5%", (t) => new Randomizer(t, 0.05, 10, 3, 0));
        yield return ("drop 5% 2", (t) => new Randomizer(t, 0.05, 0, 0, 1));
        yield return ("random delay 10ms 2", (t) => new Randomizer(t, 0.0, 10, 3, 1));
        yield return ("random delay 10ms and drop 5% 2", (t) => new Randomizer(t, 0.05, 10, 3, 1));
    }


    [TestCaseSource(nameof(TransferMutators))]
    public async Task TestSend((string, Func<IUTPTransfer, IUTPTransfer>) test)
    {
        (string _, Func<IUTPTransfer, IUTPTransfer> transferMutator) = test;

        int payloadSize = 3_000_000;
        byte[] data = new byte[payloadSize];
        new Random(0).NextBytes(data);

        MemoryStream input = new MemoryStream(data);
        MemoryStream output = new MemoryStream();

        PeerProxy receiverProxy = new PeerProxy();
        UTPStream sender = new UTPStream(receiverProxy, 0, logManager);
        PeerProxy senderProxy = new PeerProxy();
        senderProxy._implementation = transferMutator.Invoke(sender);
        UTPStream receiver = new UTPStream(senderProxy, 0, logManager);
        receiverProxy._implementation = transferMutator.Invoke(receiver);

        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        CancellationToken token = cts.Token;

        Stopwatch sw = Stopwatch.StartNew();

        Task senderTask = Task.Run(async () =>
        {
            await sender.InitiateHandshake(token);
            await sender.WriteStream(input, token);
        }, token);

        Task receiverTask = Task.Run(async () =>
        {
            await receiver.HandleHandshake(token);
            await receiver.ReadStream(output, token);
        }, token);

        await Task.WhenAll(senderTask, receiverTask);
        Console.Error.WriteLine($"Took {sw.Elapsed}. Resend ratio {(double)senderProxy._totalBytesSent / (double)payloadSize}, {(double)receiverProxy._totalBytesSent / (double)payloadSize}");

        Assert.That(data, Is.EqualTo(output.ToArray()));
    }

    internal class PeerProxy : IUTPTransfer
    {
        internal IUTPTransfer? _implementation;
        internal int _totalBytesSent = 0;

        public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
        {
            _totalBytesSent += data.Length;
            return _implementation!.ReceiveMessage(packetHeader, data, token);
        }
    }

    internal class Randomizer(IUTPTransfer actual, double dropPercentage, int baseDelayMs, int randomDelayMs, int seed) : IUTPTransfer
    {
        private ILogger _logger = logManager.GetClassLogger<Randomizer>();
        private Random _random = new Random(seed);
        private SpinLock _lock = new SpinLock();

        public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
        {
            byte[] dataArr = data.ToArray();

            // Dont block
            _ = Task.Run(async () =>
            {
                // skipped
                if (_random.NextDouble() < dropPercentage)
                {
                    if (_logger.IsTrace) _logger.Trace($"T Drop {packetHeader}");
                    return;
                }

                if (baseDelayMs != 0)
                    await Task.Delay(baseDelayMs, token);
                if (randomDelayMs != 0)
                    await Task.Delay( _random.Next() % randomDelayMs, token);

                if (_logger.IsTrace) _logger.Trace($"T Send {packetHeader}");
                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);
                    await actual.ReceiveMessage(packetHeader, dataArr, token);
                }
                finally
                {
                    _lock.Exit();
                }
            }, token);

            return Task.CompletedTask;
        }
    }
}
