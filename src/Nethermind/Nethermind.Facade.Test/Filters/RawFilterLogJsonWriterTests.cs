// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Filters;

[Parallelizable(ParallelScope.All)]
public class RawFilterLogJsonWriterTests
{
    private const ulong BlockNumber = 0x1234;
    private const ulong BlockTimestamp = 0x65f0a1b2;
    private static readonly Hash256 BlockHash = TestItem.KeccakA;
    private static readonly Hash256 TransactionHash = TestItem.KeccakB;
    private static readonly EthereumJsonSerializer Serializer = new();

    public enum Source
    {
        Object,
        ObjectView,
        Compact,
        Full,
    }

    public static IEnumerable<TestCaseData> Logs() =>
        from log in LogCases()
        from source in Enum.GetValues<Source>()
        select new TestCaseData(log.Log, log.LogIndex, log.TransactionIndex, source).SetArgDisplayNames(log.Name, source.ToString());

    private static IEnumerable<(string Name, LogEntry Log, long LogIndex, int TransactionIndex)> LogCases()
    {
        Hash256 leadingZeros = new("0x0000000000000000000000000000000000000000000000000000000000abcdef");
        byte[] zeroPrefixed = [0, 0, 0, 0, 0, .. Enumerable.Range(1, 40).Select(static i => (byte)i)];
        yield return Case("no data, no topics", Address.Zero, [], [], logIndex: 0, transactionIndex: 0);
        yield return Case("one zero byte", TestItem.AddressA, [0], [TestItem.KeccakC]);
        yield return Case("zero-prefixed data", TestItem.AddressB, zeroPrefixed, [TestItem.KeccakC, Keccak.Zero]);
        yield return Case("all-zero data", TestItem.AddressC, new byte[64], [Keccak.Zero, leadingZeros]);
        yield return Case("four topics", TestItem.AddressD, [1, 2, 3], [TestItem.KeccakC, TestItem.KeccakD, leadingZeros, Keccak.Zero]);
        yield return Case("data above the stack buffer", TestItem.AddressE, Bytes(300), [TestItem.KeccakE]);
        yield return Case("data above the chunk threshold", TestItem.AddressF, Bytes(5000), [TestItem.KeccakF], logIndex: 1_000_000, transactionIndex: 1023);
    }

    [TestCaseSource(nameof(Logs))]
    public void Writes_the_same_bytes_as_the_filter_log_serializer(LogEntry log, long logIndex, int transactionIndex, Source source)
    {
        string expected = Serializer.Serialize(new FilterLog(logIndex, BlockNumber, BlockTimestamp, BlockHash, transactionIndex, TransactionHash, log.Address, log.Data, log.Topics));

        ArrayBufferWriter<byte> writer = new();
        Write(writer, source, log, logIndex, transactionIndex);

        Assert.That(Encoding.UTF8.GetString(writer.WrittenSpan), Is.EqualTo(expected));
    }

    [Test]
    public void Chunk_separates_logs_with_commas()
    {
        LogEntry log = new(TestItem.AddressA, [1], [TestItem.KeccakC]);
        using LogsJsonChunk chunk = new();
        for (int i = 0; i < 3; i++)
        {
            chunk.StartLog();
            RawFilterLogJsonWriter.Write(chunk, i, BlockNumber, BlockTimestamp, BlockHash, 0, TransactionHash, log);
        }

        string expected = string.Join(",", Enumerable.Range(0, 3).Select(static i =>
            Serializer.Serialize(new FilterLog(i, BlockNumber, BlockTimestamp, BlockHash, 0, TransactionHash, TestItem.AddressA, [1], [TestItem.KeccakC]))));
        Assert.That(chunk.Count, Is.EqualTo(3));
        Assert.That(Encoding.UTF8.GetString(chunk.Json), Is.EqualTo(expected));
    }

    private static void Write(IBufferWriter<byte> writer, Source source, LogEntry log, long logIndex, int transactionIndex)
    {
        switch (source)
        {
            case Source.Object:
                RawFilterLogJsonWriter.Write(writer, logIndex, BlockNumber, BlockTimestamp, BlockHash, transactionIndex, TransactionHash, log);
                break;
            case Source.ObjectView:
                {
                    LogEntryStructRef view = new(log);
                    RawFilterLogJsonWriter.Write(writer, logIndex, BlockNumber, BlockTimestamp, BlockHash, transactionIndex, TransactionHash, ref view);
                    break;
                }
            case Source.Compact:
                {
                    RlpReader reader = new(CompactLogEntryDecoder.Instance.EncodeAsBytes(log, RlpBehaviors.Storage));
                    CompactLogEntryDecoder.DecodeLogEntryStructRef(ref reader, RlpBehaviors.Storage, out LogEntryStructRef stored);
                    RawFilterLogJsonWriter.Write(writer, logIndex, BlockNumber, BlockTimestamp, BlockHash, transactionIndex, TransactionHash, ref stored);
                    break;
                }
            case Source.Full:
                {
                    RlpReader reader = new(LogEntryDecoder.Instance.EncodeAsBytes(log));
                    LogEntryDecoder.DecodeStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef stored);
                    RawFilterLogJsonWriter.Write(writer, logIndex, BlockNumber, BlockTimestamp, BlockHash, transactionIndex, TransactionHash, ref stored);
                    break;
                }
        }
    }

    private static (string, LogEntry, long, int) Case(string name, Address address, byte[] data, Hash256[] topics, long logIndex = 7, int transactionIndex = 3) =>
        (name, new LogEntry(address, data, topics), logIndex, transactionIndex);

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(static i => (byte)(i * 31 + 7)).ToArray();
}
