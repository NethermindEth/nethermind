using System.Buffers;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Logs.Test;

public class LogBuilderTests
{
    private const int HashCount = 256;
    private readonly Hash256[] _hashes;
    private static readonly int BucketOverhead1Byte = LogsBuilder.Bucket1Bytes.Count * 4;

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

        (var reader, int written) = BuildToReader<LogsBuilder.Bucket1Bytes>(builder);

        LogsBuilder.Entry e1 = new(block, tx1);
        LogsBuilder.Entry e2 = new(block, tx2);

        reader.Find(Address.SystemUser).ToArray().Should().BeEquivalentTo([e1]);
        reader.Find(Address.MaxValue).ToArray().Should().BeEquivalentTo([e2]);

        reader.Find(hash0).ToArray().Should().BeEquivalentTo([e1, e2]);

        reader.Find(hash0, 1).Should().BeEmpty();

        written.Should().Be(BucketOverhead1Byte + 30);
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

        (var reader, int written) = BuildToReader<LogsBuilder.Bucket1Bytes>(builder);

        LogsBuilder.Entry e1 = new(block1, tx1);
        LogsBuilder.Entry e2 = new(block1, tx2);
        LogsBuilder.Entry e3 = new(block2, tx2);

        // order by number of block then by tx
        LogsBuilder.Entry[] expected = [e1, e2, e3];

        reader.Find(Address.SystemUser).Should().BeEquivalentTo(expected);
        reader.Find(hash0).ToArray().Should().BeEquivalentTo(expected);

        reader.Find(hash0, 1).Should().BeEmpty();

        written.Should().Be(BucketOverhead1Byte + 28);
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

        (var reader, int written) = BuildToReader<LogsBuilder.Bucket1Bytes>(builder);

        reader.Find(Address.SystemUser)
            .Should()
            .BeEquivalentTo(Builder().Select(t => new LogsBuilder.Entry(t.block, t.tx)));

        Console.WriteLine($"{(double)written / logEntries:F1} bytes per {nameof(LogEntry)}");
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

    [TestCase(false, TestName = "Don't repeat any topic")]
    [TestCase(true, TestName = "Repeat first topic in all")]
    public void Random(bool repeat1Topic)
    {
        var random = new Random(5347);
        var builder = new LogsBuilder();

        const uint blocks = 20_000;
        const uint txs = 200;
        const int maxLogs = 3;

        var entries = 0;

        var address = new Address(new byte[Address.Size]);
        Span<byte> hash = stackalloc byte[Hash256.Size];

        for (uint block = 0; block < blocks; block++)
        {
            for (ushort tx = 0; tx < txs; tx++)
            {
                random.NextBytes(address.Bytes);
                var topics = random.Next(maxLogs);

                var t = topics == 0 ? [] : new Hash256[topics];
                for (var topic = 0; topic < topics; topic++)
                {
                    if (topic == 0 && repeat1Topic)
                    {
                        t[topic] = Keccak.OfAnEmptyString;
                    }
                    else
                    {
                        random.NextBytes(hash);
                        t[topic] = new Hash256(hash);
                    }
                }

                builder.Append(new LogEntry(address, [], t), block, tx);
                entries += 1 + t.Length;
            }
        }

        (var reader, int written) = BuildToReader<LogsBuilder.Bucket2Bytes>(builder);

        Console.WriteLine($"{entries} entries written resulting in {ToMB(written):F1}MB of data");
        var perEntry = (double)written / entries;
        Console.WriteLine($"{perEntry:F1} bytes per {nameof(LogEntry)}");
        Console.WriteLine($"Saving 1 billion of events would use {ToMB(perEntry * 1_000_000_000):F1}MB");
    }

    [Test]
    public void Large_topic_count()
    {
        const int blocks = 1_000;
        const int txs = 100;
        const int seed = 5347;

        var random = new Random(seed);
        var builder = new LogsBuilder();

        var address = new Address(new byte[Address.Size]);
        Span<byte> hash = stackalloc byte[Hash256.Size];

        for (uint block = 0; block < blocks; block++)
        {
            for (ushort tx = 0; tx < txs; tx++)
            {
                random.NextBytes(address.Bytes);
                random.NextBytes(hash);

                builder.Append(new LogEntry(address, [], [new Hash256(hash)]), block, tx);
            }
        }

        (var reader, _) = BuildToReader<LogsBuilder.Bucket2Bytes>(builder);

        random = new Random(seed);

        for (uint block = 0; block < blocks; block++)
        {
            for (ushort tx = 0; tx < txs; tx++)
            {
                random.NextBytes(address.Bytes);
                random.NextBytes(hash);

                var entries = reader.Find(new Hash256(hash), 0).ToArray();
                entries.Should().BeEquivalentTo([new LogsBuilder.Entry(block, tx)], $"@ block: {block},  tx: {tx}");
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

        (var reader, int written) = BuildToReader<LogsBuilder.Bucket1Bytes>(builder);

        LogsBuilder.Entry e = new(block, tx);

        reader.Find(Address.SystemUser).ToArray().Should().BeEquivalentTo([e]);
        written.Should().Be(BucketOverhead1Byte + 22);
    }

    [Test]
    [Explicit]
    //[Ignore("no import atm")]
    public void Import()
    {
        ImportImpl<LogsBuilder.Bucket2Bytes>();
        return;

        static void ImportImpl<TBucketing>()
            where TBucketing : LogsBuilder.IBucketing
        {
            var builder = new LogsBuilder();
            const int range = 50_000;

            Importer.Import("E:\\nethermind\\nethermind_db\\mainnet", builder, range);

            var prefixes = new ArrayBufferWriter<byte>(TBucketing.Count * 4);
            var hashesAndPointers = new ArrayBufferWriter<byte>(1024 * 1024);
            var compressed = new ArrayBufferWriter<byte>(1024 * 1024);
            builder.Build<TBucketing>(prefixes, hashesAndPointers, compressed);

            var reader = new LogsBuilder.MemoryReader<TBucketing>(prefixes.WrittenMemory,
                hashesAndPointers.WrittenMemory,
                compressed.WrittenMemory);

            var totalWritten = prefixes.WrittenCount + hashesAndPointers.WrittenCount + compressed.WrittenCount;

            Console.WriteLine($"Processing last {range} blocks using {nameof(TBucketing)}:");
            Console.WriteLine(
                $"- indexed entries: {builder.Count}, which gives {builder.Count / range:F0} entries/block");
            Console.WriteLine($"- max size of an index bucket: {ToMB(reader.MaxBucketLength):F3}MB");
            Console.WriteLine(
                $"- total index size: {ToMB(totalWritten):F1}MB which gives {ToMB((double)totalWritten / range):F2}MB/block");
            Console.WriteLine(
                $"  - {nameof(prefixes)}: {ToMB(prefixes.WrittenCount):F1}MB");
            Console.WriteLine(
                $"  - {nameof(hashesAndPointers)}: {ToMB(hashesAndPointers.WrittenCount):F1}MB");
            Console.WriteLine(
                $"  - {nameof(compressed)}: {ToMB(compressed.WrittenCount):F1}MB");

            Console.WriteLine();
            Console.WriteLine("Enumerating: Transfer (index_topic_1 address src, index_topic_2 address dst, uint256 wad)");
            var transfer = new Hash256("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");

            var memCount = reader.Find(transfer, 0).Count();

            Console.WriteLine($"Found {memCount} Transfer entries using Memory reader");

            const string dir = "import";
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);

            var files = CreateFiles(dir);

            try
            {
                RandomAccess.Write(files.prefixes, prefixes.WrittenSpan, 0);
                RandomAccess.Write(files.hashes, hashesAndPointers.WrittenSpan, 0);
                RandomAccess.Write(files.compressed, compressed.WrittenSpan, 0);

                RandomAccess.FlushToDisk(files.prefixes);
                RandomAccess.FlushToDisk(files.hashes);
                RandomAccess.FlushToDisk(files.compressed);

                var fileReader = new LogsBuilder.FileReader<TBucketing>(files.prefixes, files.hashes, files.compressed);

                var fileCount = fileReader.Find(transfer, 0).Count();

                Console.WriteLine($"Found {fileCount} entries using File reader");

            }
            finally
            {
                files.compressed.Dispose();
                files.prefixes.Dispose();
                files.hashes.Dispose();
            }
        }

        static (SafeFileHandle prefixes, SafeFileHandle hashes, SafeFileHandle compressed) CreateFiles(string dir)
        {
            const FileOptions options = FileOptions.RandomAccess | FileOptions.WriteThrough;
            const FileMode mode = FileMode.CreateNew;
            const FileAccess access = FileAccess.ReadWrite;
            const FileShare share = FileShare.None;
            return (
                File.OpenHandle(Path.Combine(dir, "prefixes.data"), mode, access, share, options),
                File.OpenHandle(Path.Combine(dir, "hashes.data"), mode, access, share, options),
                File.OpenHandle(Path.Combine(dir, "compressed.data"), mode, access, share, options));
        }
    }

    private static double ToMB(double totalWritten)
    {
        return totalWritten / 1024 / 1024;
    }

    private static (LogsBuilder.MemoryReader<TBucketing> reader, int writtenCount) BuildToReader<TBucketing>(
        LogsBuilder builder)
        where TBucketing : LogsBuilder.IBucketing
    {
        var prefixes = new ArrayBufferWriter<byte>();
        var hashesAndPointers = new ArrayBufferWriter<byte>();
        var compressed = new ArrayBufferWriter<byte>();

        builder.Build<TBucketing>(prefixes, hashesAndPointers, compressed);

        var reader = new LogsBuilder.MemoryReader<TBucketing>(prefixes.WrittenMemory, hashesAndPointers.WrittenMemory,
            compressed.WrittenMemory);

        return (reader, writtenCount: prefixes.WrittenCount + hashesAndPointers.WrittenCount + compressed.WrittenCount);
    }
}
