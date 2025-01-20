using System.Buffers;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Logs.Test;

public class LogBuilderTests
{
    private const int HashCount = 256;
    private readonly Hash256[] _hashes;

    public LogBuilderTests()
    {
        var random = new Random(13);
        Span<byte> span = stackalloc byte[Hash256.Size];

        _hashes = new Hash256[HashCount];

        for (int i = 0; i < HashCount; i++)
        {
            random.NextBytes(span);
            _hashes[i] = new Hash256(span);
        }
    }

    [Test]
    public void Simple()
    {
        var builder = new LogsBuilder();
        var hash0 = _hashes[0];

        var entry0 = new LogEntry(Address.SystemUser, [], [hash0]);
        var entry1 = new LogEntry(Address.MaxValue, [], [hash0]);

        const ushort block = 1;
        const ushort tx1 = 1;
        const ushort tx2 = 2;

        builder.Append(entry0, block, tx1);
        builder.Append(entry1, block, tx2);

        var writer = new ArrayBufferWriter<byte>();

        builder.Build(writer);

        var reader = new LogsBuilder.MemoryReader(writer.WrittenMemory);

        LogsBuilder.Entry e1 = new(block, tx1);
        LogsBuilder.Entry e2 = new(block, tx2);

        reader.Find(Address.SystemUser).ToArray().Should().BeEquivalentTo([e1]);
        reader.Find(Address.MaxValue).ToArray().Should().BeEquivalentTo([e2]);

        reader.Find(hash0).ToArray().Should().BeEquivalentTo([e1, e2]);

        reader.Find(hash0, 1).Should().BeEmpty();
    }

    [Test]
    public void Entries_are_always_ordered_per_block()
    {
        var builder = new LogsBuilder();
        var hash0 = _hashes[0];

        var entry = new LogEntry(Address.SystemUser, [], [hash0]);

        const ushort block1 = 1;
        const ushort block2 = 2;
        const ushort tx1 = 1;
        const ushort tx2 = 2;

        // Report it with different ordering
        builder.Append(entry, block2, tx2);
        builder.Append(entry, block1, tx2);
        builder.Append(entry, block1, tx1);

        var writer = new ArrayBufferWriter<byte>();

        builder.Build(writer);

        var reader = new LogsBuilder.MemoryReader(writer.WrittenMemory);

        LogsBuilder.Entry e1 = new(block1, tx1);
        LogsBuilder.Entry e2 = new(block1, tx2);
        LogsBuilder.Entry e3 = new(block2, tx2);

        // order by number of block then by tx
        LogsBuilder.Entry[] expected = [e1, e2, e3];

        reader.Find(Address.SystemUser).Should().BeEquivalentTo(expected);
        reader.Find(hash0).ToArray().Should().BeEquivalentTo(expected);

        reader.Find(hash0, 1).Should().BeEmpty();
    }

    [Test]
    public void Frequent_topics_are_compressed_well()
    {
        var builder = new LogsBuilder();
        var hash0 = _hashes[0];

        var entry = new LogEntry(Address.SystemUser, [], [hash0]);

        const int blocks = 1000;
        const int txs = 1000;
        const int logEntries = blocks * txs;

        for (uint i = 0; i < blocks; i++)
        {
            for (ushort j = 0; j < txs; j++)
            {
                builder.Append(entry, i, j);
            }
        }

        var writer = new ArrayBufferWriter<byte>();

        builder.Build(writer);

        Console.WriteLine($"{(double)writer.WrittenCount / logEntries:F1} bytes per {nameof(LogEntry)}");
    }
}
