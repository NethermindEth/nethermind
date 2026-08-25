// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Text;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;
using ZstdSharp;

namespace Nethermind.Tools.Kute.Test.Replay;

public class TraceLineReaderTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Directory.CreateTempSubdirectory(nameof(TraceLineReaderTests)).FullName;

    [TearDown]
    public void TearDown() =>
        Directory.Delete(_directory, recursive: true);

    [TestCase(".jsonl", TestName = "Plain")]
    [TestCase(".jsonl.gz", TestName = "Gzip")]
    [TestCase(".jsonl.zst", TestName = "Zstd")]
    public void Reads_records_from_every_supported_encoding(string extension)
    {
        string[] records = ["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"];
        string path = Write(extension, string.Join('\n', records) + '\n');

        Assert.That(ReadAll(path), Is.EqualTo(records));
    }

    [TestCase("a\nb\n", TestName = "Trailing newline")]
    [TestCase("a\nb", TestName = "No trailing newline")]
    [TestCase("a\r\nb\r\n", TestName = "Windows line endings")]
    [TestCase("a\n\n\nb\n", TestName = "Blank lines between records")]
    [TestCase("\na\nb\n", TestName = "Leading blank line")]
    public void Skips_blank_lines_and_trims_carriage_returns(string content)
    {
        // Captures come from several writers, so a blank line or a stray carriage return must not
        // become a record: an empty body would be sent as a request and counted as a failure.
        string path = Write(".jsonl", content);

        Assert.That(ReadAll(path), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Reads_a_record_larger_than_the_initial_buffer()
    {
        // Captured eth_call records reach a couple of megabytes, well past the starting buffer.
        string large = new('x', 5 * 1024 * 1024);
        string path = Write(".jsonl", $"small\n{large}\nsmall\n");

        IReadOnlyList<string> records = ReadAll(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Has.Count.EqualTo(3));
            Assert.That(records[1], Has.Length.EqualTo(large.Length));
            Assert.That(records[1], Is.EqualTo(large));
        }
    }

    [Test]
    public void Counts_records_as_they_are_read()
    {
        string path = Write(".jsonl", "a\nb\nc\n");

        using TraceLineReader reader = new(path);
        Assert.That(reader.RecordsRead, Is.Zero);

        reader.TryReadRecord(out _);
        Assert.That(reader.RecordsRead, Is.EqualTo(1));

        while (reader.TryReadRecord(out _))
        {
        }

        Assert.That(reader.RecordsRead, Is.EqualTo(3));
    }

    [Test]
    public void Reports_end_of_file_repeatedly()
    {
        string path = Write(".jsonl", "a\n");

        using TraceLineReader reader = new(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryReadRecord(out _), Is.True);
            Assert.That(reader.TryReadRecord(out _), Is.False);
            Assert.That(reader.TryReadRecord(out _), Is.False);
        }
    }

    [Test]
    public void Reads_an_empty_file()
    {
        string path = Write(".jsonl", string.Empty);

        Assert.That(ReadAll(path), Is.Empty);
    }

    private static IReadOnlyList<string> ReadAll(string path)
    {
        List<string> records = [];
        using TraceLineReader reader = new(path);
        while (reader.TryReadRecord(out ReadOnlySpan<byte> record))
        {
            records.Add(Encoding.UTF8.GetString(record));
        }

        return records;
    }

    private string Write(string extension, string content)
    {
        string path = Path.Combine(_directory, $"trace{extension}");
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        using FileStream file = File.Create(path);
        switch (Path.GetExtension(path))
        {
            case ".zst":
                using (CompressionStream compressor = new(file))
                {
                    compressor.Write(bytes);
                }

                break;
            case ".gz":
                using (GZipStream compressor = new(file, CompressionLevel.Fastest))
                {
                    compressor.Write(bytes);
                }

                break;
            default:
                file.Write(bytes);
                break;
        }

        return path;
    }
}
