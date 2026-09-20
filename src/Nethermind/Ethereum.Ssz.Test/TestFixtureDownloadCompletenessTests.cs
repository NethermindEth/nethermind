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

/// <summary>
/// A dropped connection leaves gzip reporting a clean end of stream and the tar reader seeing no
/// further entries, so a partial archive looks complete. Marking it complete caches the truncation
/// until a human deletes the directory, and every suite reading it reports green on a fraction of
/// its vectors: the ssz_generic suite ran 10 of 2519 that way without failing.
/// </summary>
[TestFixture]
public class TestFixtureDownloadCompletenessTests
{
    private const string EntryPath = "tests/general/phase0/ssz_generic/uints/valid/uint_8.data";
    private static readonly byte[] EntryContent = [1, 2, 3, 4];

    [Test]
    public void A_body_shorter_than_its_content_length_is_refused_and_leaves_no_completion_marker()
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length + 4096);
        string suite = "TruncatedDownloadTest";
        string target = CachePathFor(suite);

        try
        {
            IOException ex = Assert.Throws<IOException>(
                () => TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz"))!;

            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Does.Contain("truncated"));
                Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.False,
                    "a truncated archive marked complete is cached and read as green by every suite using it");
            });
        }
        finally
        {
            Cleanup(target);
        }
    }

    [Test]
    public void A_complete_body_extracts_and_is_marked_complete()
    {
        byte[] archive = BuildArchive();
        using StubArchiveServer server = new(archive, contentLength: archive.Length);
        string suite = "CompleteDownloadTest";
        string target = CachePathFor(suite);

        try
        {
            string extracted = TestFixtureDownloader.EnsureDownloaded(suite, server.UrlTemplate, "v0", "general.tar.gz");

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.True);
                Assert.That(File.ReadAllBytes(Path.Combine(extracted, EntryPath.Replace('/', Path.DirectorySeparatorChar))),
                    Is.EqualTo(EntryContent));
            });
        }
        finally
        {
            Cleanup(target);
        }
    }

    private static string CachePathFor(string suite) =>
        Path.Combine(Path.GetTempPath(), "nethermind-eest", suite, "v0", "general");

    private static void Cleanup(string target)
    {
        string suiteRoot = Path.GetFullPath(Path.Combine(target, "..", ".."));
        if (Directory.Exists(suiteRoot)) Directory.Delete(suiteRoot, true);
    }

    private static byte[] BuildArchive()
    {
        using MemoryStream tar = new();
        using (TarWriter writer = new(tar, leaveOpen: true))
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, EntryPath) { DataStream = new MemoryStream(EntryContent) };
            writer.WriteEntry(entry);
        }

        tar.Position = 0;
        using MemoryStream gz = new();
        using (GZipStream compressor = new(gz, CompressionMode.Compress, leaveOpen: true))
        {
            tar.CopyTo(compressor);
        }

        return gz.ToArray();
    }

    /// <summary>
    /// A raw socket rather than HttpListener, which needs a URL reservation on Windows. Serves one
    /// request with a caller-chosen Content-Length so a short body can be produced deliberately.
    /// </summary>
    private sealed class StubArchiveServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;

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
