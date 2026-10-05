// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class TestFixtureDownloadCompletenessTests
{
    private const string EntrySubtree = "tests/general/phase0/ssz_generic";
    private const string EntryPath = EntrySubtree + "/uints/valid/uint_8.data";
    private const string SecondSubtree = "tests/general/phase0/ssz_static";
    private const string SecondEntryPath = SecondSubtree + "/Checkpoint/ssz_random/case_0/serialized.ssz_snappy";
    private static readonly byte[] EntryContent = [1, 2, 3, 4];

    [Test]
    public void A_body_shorter_than_its_content_length_is_refused_and_leaves_no_completion_marker([Values] bool selective)
    {
        byte[] archive = BuildArchive(64 * 1024, EntryPath);
        using StubArchiveServer server = new(archive, contentLength: archive.Length + 4096);
        using CacheCleanup cleanup = CreateCache("TruncatedDownloadTest", out string suite, out string target);
        IOException ex = Assert.Catch<IOException>(
            () => TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz",
                selective ? _ => true : null))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.Message, Does.Contain("expected"));
        Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.False,
            "a truncated archive marked complete is cached and read as green by every suite using it");
    }

    [TestCase(0, false)]
    [TestCase(64 * 1024, false)]
    [TestCase(64 * 1024, true)]
    public void A_complete_body_extracts_and_is_marked_complete(int trailingPadding, bool selective)
    {
        byte[] archive = BuildArchive(trailingPadding, EntryPath);
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache("CompleteDownloadTest", out string suite, out string target);
        string extracted = TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz",
            selective ? _ => true : null);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.True);
        Assert.That(File.ReadAllLines(Path.Combine(target, ".completed")), Is.EqualTo(new[] { "v0", EntrySubtree }),
            "the marker must record the version and every extracted subtree, so a later loss of a subtree is detectable");
        Assert.That(File.ReadAllBytes(Path.Combine(target, EntryPath.Replace('/', Path.DirectorySeparatorChar))),
            Is.EqualTo(EntryContent));
        Assert.That(File.ReadAllBytes(Path.Combine(extracted, EntryPath.Replace('/', Path.DirectorySeparatorChar))),
            Is.EqualTo(EntryContent));
    }

    [Test]
    public void A_marker_over_a_directory_with_no_content_is_treated_as_absent_and_the_archive_is_downloaded_again()
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache("EmptyCacheTest", out string suite, out string target);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, ".completed"), "v0");

        string extracted = TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(server.RequestCount, Is.EqualTo(1),
            "a marker over an empty directory must not short-circuit the download: nothing under it can be run");
        Assert.That(File.Exists(Path.Combine(extracted, EntryPath.Replace('/', Path.DirectorySeparatorChar))), Is.True,
            "the re-download must actually repopulate the cache");
        Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_marker_over_a_populated_directory_is_still_honored_without_a_download(bool recordsSubtree)
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache("PopulatedCacheTest", out string suite, out string target);
        string entryOnDisk = Path.Combine(target, EntryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(entryOnDisk)!);
        File.WriteAllBytes(entryOnDisk, EntryContent);
        File.WriteAllLines(Path.Combine(target, ".completed"), recordsSubtree ? ["v0", EntrySubtree] : ["v0"]);

        TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz");

        Assert.That(server.RequestCount, Is.EqualTo(0),
            "the content check must only defeat the fast path when there is nothing to run, not on every call");
    }

    [Test]
    public void A_marker_over_a_cache_missing_a_recorded_subtree_is_treated_as_absent_and_the_archive_is_downloaded_again()
    {
        byte[] archive = BuildArchive(EntryPath, SecondEntryPath);
        using CacheCleanup cleanup = CreateCache("MissingSubtreeCacheTest", out string suite, out string target);
        using (StubArchiveServer first = new(archive, contentLength: archive.Length))
        {
            TestFixtureDownloader.EnsureDownloaded(suite, first.UrlTemplate, "v0", "general.tar.gz");
        }
        Directory.Delete(Path.Combine(target, SecondSubtree.Replace('/', Path.DirectorySeparatorChar)), true);

        using StubArchiveServer second = new(archive, contentLength: archive.Length);
        TestFixtureDownloader.EnsureDownloaded(suite, second.UrlTemplate, "v0", "general.tar.gz");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(second.RequestCount, Is.EqualTo(1),
            "a marker over a cache missing a recorded subtree must not short-circuit the download: every suite over that subtree runs zero vectors");
        Assert.That(File.Exists(Path.Combine(target, SecondEntryPath.Replace('/', Path.DirectorySeparatorChar))), Is.True,
            "the re-download must restore the missing subtree");
    }

    [Test]
    public void A_marker_written_for_a_different_extraction_tag_is_treated_as_absent_and_the_archive_is_downloaded_again()
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache("StaleTagCacheTest", out string suite, out string target);
        string staleEntry = Path.Combine(target, "tests", "general", "phase0", "ssz_generic", "stale.data");
        Directory.CreateDirectory(Path.GetDirectoryName(staleEntry)!);
        File.WriteAllBytes(staleEntry, [9]);
        File.WriteAllText(Path.Combine(target, ".completed"), "ssz_static=*");

        TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz", _ => true, "ssz_static=*;operations=fulu");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(server.RequestCount, Is.EqualTo(1),
            "a marker for a narrower filter must not short-circuit the download: the subtrees the wider filter keeps are missing");
        Assert.That(File.Exists(staleEntry), Is.False, "the stale extraction must be replaced, not merged into");
        Assert.That(File.ReadAllLines(Path.Combine(target, ".completed"))[0], Is.EqualTo("ssz_static=*;operations=fulu"),
            "the marker must record the filter that produced the extraction");
    }

    [TestCase(null, 0)]
    [TestCase("ssz_static=*", 1)]
    public void A_marker_holding_the_version_is_honored_only_by_untagged_callers(string? extractionTag, int expectedDownloads)
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache(extractionTag is null ? "VersionMarkerUntaggedTest" : "VersionMarkerTaggedTest", out string suite, out string target);
        string entryOnDisk = Path.Combine(target, EntryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(entryOnDisk)!);
        File.WriteAllBytes(entryOnDisk, EntryContent);
        File.WriteAllText(Path.Combine(target, ".completed"), "v0");

        TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz", null, extractionTag);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(server.RequestCount, Is.EqualTo(expectedDownloads));
        Assert.That(File.ReadAllLines(Path.Combine(target, ".completed"))[0], Is.EqualTo(extractionTag ?? "v0"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void An_archive_that_yields_no_files_is_refused_and_leaves_no_completion_marker(bool selective)
    {
        byte[] archive = selective ? BuildArchive() : BuildDirectoryOnlyArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        using CacheCleanup cleanup = CreateCache(selective ? "FilteredToNothingTest" : "DirectoryOnlyArchiveTest", out string suite, out string target);
        IOException ex = Assert.Throws<IOException>(
            () => TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz",
                selective ? _ => false : null))!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.Message, Does.Contain("no files"));
        Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.False,
            "an empty extraction marked complete is exactly the cache state every suite then reads as green");
    }

    // Cache roots are shared across processes; fixed suite names let concurrent runs delete each other's directories.
    private static CacheCleanup CreateCache(string name, out string suite, out string target)
    {
        suite = $"{name}-{Guid.NewGuid():N}";
        target = Path.Combine(Path.GetTempPath(), "nethermind-eest", suite, "v0", "general");
        return new(target);
    }

    private static void Cleanup(string target)
    {
        string suiteRoot = Path.GetFullPath(Path.Combine(target, "..", ".."));
        if (Directory.Exists(suiteRoot)) Directory.Delete(suiteRoot, true);
    }

    private sealed class CacheCleanup(string target) : IDisposable
    {
        public void Dispose() => Cleanup(target);
    }

    private static byte[] BuildArchive() => BuildArchive(EntryPath);

    private static byte[] BuildArchive(params string[] entryPaths) => BuildArchive(0, entryPaths);

    private static byte[] BuildArchive(int trailingPadding, params string[] entryPaths) => BuildArchive(trailingPadding, writer =>
    {
        foreach (string entryPath in entryPaths)
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, entryPath) { DataStream = new MemoryStream(EntryContent) };
            writer.WriteEntry(entry);
        }
    });

    private static byte[] BuildArchive(int trailingPadding, Action<TarWriter> writeEntries)
    {
        using MemoryStream tar = new();
        using (TarWriter writer = new(tar, leaveOpen: true)) writeEntries(writer);

        tar.SetLength(tar.Length + trailingPadding);
        tar.Position = 0;
        using MemoryStream gz = new();
        using (GZipStream compressor = new(gz, trailingPadding == 0 ? CompressionLevel.Optimal : CompressionLevel.NoCompression, leaveOpen: true))
        {
            tar.CopyTo(compressor);
        }

        return gz.ToArray();
    }

    private static byte[] BuildDirectoryOnlyArchive() => BuildArchive(0,
        writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "tests/general/phase0/ssz_generic/")));

    // HttpListener requires a URL reservation on Windows.
    private sealed class StubArchiveServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private int _requestCount;

        public StubArchiveServer(byte[] body, int contentLength)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            UrlTemplate = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{{0}}/{{1}}";

            _thread = new Thread(() =>
            {
                try
                {
                    using TcpClient client = _listener.AcceptTcpClient();
                    Interlocked.Increment(ref _requestCount);
                    using NetworkStream stream = client.GetStream();
                    ReadRequestHeaders(stream);

                    byte[] header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/gzip\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n");
                    stream.Write(header);
                    stream.Write(body);
                    stream.Flush();
                }
                catch (Exception)
                {
                    // The client closing first is a normal end to this exchange, not a test failure.
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public string UrlTemplate { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        private static void ReadRequestHeaders(NetworkStream stream)
        {
            int matched = 0;
            ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
            while (matched < terminator.Length)
            {
                int b = stream.ReadByte();
                if (b < 0) return;
                matched = b == terminator[matched] ? matched + 1 : b == terminator[0] ? 1 : 0;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
