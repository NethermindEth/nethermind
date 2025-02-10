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

        writer.WrittenCount.Should().Be(48);
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

        writer.WrittenCount.Should().Be(50);
    }

    [Test]
    public void Frequent_topics_are_compressed_well()
    {
        var builder = new LogsBuilder();
        var hash0 = _hashes[0];

        var entry = new LogEntry(Address.SystemUser, [], [hash0]);

        const int blocks = 100;
        const int txs = 100;
        const int logEntries = blocks * txs;

        foreach ((uint block, ushort tx) in Builder())
        {
            builder.Append(entry, block, tx);
        }

        var writer = new ArrayBufferWriter<byte>();

        builder.Build(writer);

        var reader = new LogsBuilder.MemoryReader(writer.WrittenMemory);

        reader.Find(Address.SystemUser)
            .Should()
            .BeEquivalentTo(Builder().Select(t => new LogsBuilder.Entry(t.block, t.tx)));

        Console.WriteLine($"{(double)writer.WrittenCount / logEntries:F1} bytes per {nameof(LogEntry)}");
        return;

        static IEnumerable<(uint block, ushort tx)> Builder()
        {
            for (uint i = 1; i < blocks; i++)
            {
                for (ushort j = 1; j < txs; j++)
                {
                    yield return (i, j);
                }
            }
        }
    }

    [Test]
    public void Repeated_entries_should_be_deduplicated()
    {
        var builder = new LogsBuilder();

        var entry = new LogEntry(Address.SystemUser, [], [_hashes[0]]);

        const int logsReported = 1000;
        const uint block = 1;
        const ushort tx = 1;

        // Report a lot of times with the same position, to replicate a complex exchange where a lot of
        // Transfer (index_topic_1 address src, index_topic_2 address dst, uint256 wad) is done.
        for (ushort i = 0; i < logsReported; i++)
        {
            builder.Append(entry, block, tx);
        }

        var writer = new ArrayBufferWriter<byte>();

        builder.Build(writer);

        var reader = new LogsBuilder.MemoryReader(writer.WrittenMemory);

        LogsBuilder.Entry e = new(block, tx);

        reader.Find(Address.SystemUser).ToArray().Should().BeEquivalentTo([e]);
        writer.WrittenCount.Should().Be(42);
    }
}
