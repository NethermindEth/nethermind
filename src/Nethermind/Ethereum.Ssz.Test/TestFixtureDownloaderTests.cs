// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class TestFixtureDownloaderTests
{
    [Test]
    public async Task An_archive_is_completed_only_when_its_digest_matches([Values] bool matches)
    {
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter tar = new(gzip, leaveOpen: true))
        using (MemoryStream value = new([1, 2, 3]))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "fixture.bin") { DataStream = value });
        byte[] archive = compressed.ToArray();
        string digest = matches ? Convert.ToHexString(SHA256.HashData(archive)) : new string('0', 64);
        string suite = $"DigestTest-{Guid.NewGuid():N}";
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using NetworkStream stream = client.GetStream();
            using StreamReader request = new(stream, leaveOpen: true);
            while (await request.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {archive.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(archive, timeout.Token);
        }, timeout.Token);
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/fixture";
        string target = Path.Combine(Path.GetTempPath(), "nethermind-eest", suite, "v1", "fixtures");
        try
        {
            if (matches)
                Assert.That(TestFixtureDownloader.EnsureDownloaded(suite, url, "v1", "fixtures.tar.gz", expectedSha256: digest), Is.EqualTo(target));
            else
                Assert.That(() => TestFixtureDownloader.EnsureDownloaded(suite, url, "v1", "fixtures.tar.gz", expectedSha256: digest), Throws.TypeOf<IOException>());
            Assert.That(File.Exists(Path.Combine(target, ".completed")), Is.EqualTo(matches));
            await server;
        }
        finally
        {
            await timeout.CancelAsync();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [TestCase("tests/general/phase0/ssz_generic", "tests/general/phase0/ssz_generic", true, Description = "exact match")]
    [TestCase("tests/general/phase0/ssz_generic/uints/valid/uint_8.data", "tests/general/phase0/ssz_generic", true, Description = "descendant file")]
    [TestCase("tests/general/phase0/ssz_generic/", "tests/general/phase0/ssz_generic", true, Description = "descendant with trailing slash matching the directory entry itself")]
    [TestCase("tests/general/phase0/ssz_generic_extra/uints", "tests/general/phase0/ssz_generic", false, Description = "sibling that merely shares the prefix string must not match")]
    [TestCase("tests/general/phase0/ssz_static/uints", "tests/general/phase0/ssz_generic", false, Description = "unrelated sibling directory")]
    [TestCase("tests/general/phase0", "tests/general/phase0/ssz_generic", false, Description = "ancestor of the prefix is not under it")]
    [TestCase("./tests/general/phase0/ssz_generic/uints", "tests/general/phase0/ssz_generic", true, Description = "leading './' from GNU tar is stripped")]
    [TestCase("tests\\general\\phase0\\ssz_generic\\uints", "tests/general/phase0/ssz_generic", true, Description = "backslash separators are normalized")]
    [TestCase("tests/general/phase0/ssz_generic/uints", "./tests/general/phase0/ssz_generic/", true, Description = "the prefix itself is normalized too")]
    [TestCase("tests/general/altair/bls/eth_aggregate_pubkeys", "tests/general/phase0/ssz_generic", false, Description = "the unrelated content real archives carry alongside ssz_generic")]
    public void PathUnderPrefix_matches_on_directory_boundary(string entryPath, string prefix, bool expected) =>
        Assert.That(TestFixtureDownloader.PathUnderPrefix(entryPath, prefix), Is.EqualTo(expected));
}
