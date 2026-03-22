// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.EraE.Store;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Store;

/// <summary>
/// Integration tests that hit the live ethpandaops eraE server.
/// Marked [Explicit] — run manually only, never in CI.
/// They serve as a contract test: if they fail, the server format or content has changed
/// in a way that would break production import for Sepolia.
/// </summary>
[Explicit("Requires network access to data.ethpandaops.io")]
public class HttpRemoteEraClientIntegrationTests
{
    private const string SepoliaBaseUrl = "https://data.ethpandaops.io/erae/sepolia/";
    private const string ManifestFilename = "checksums_sha256.txt";

    // Epoch 0 is the smallest file (~3.5 MB) and its filename is immutable (historical data).
    private const string Epoch0Filename = "sepolia-00000-8e3e7dc9.erae";
    private const int Epoch0 = 0;
    private const int MaxEraSize = 8192;

    private TempPath _downloadDir = null!;
    private HttpRemoteEraClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _downloadDir = TempPath.GetTempDirectory();
        _client = new HttpRemoteEraClient(new Uri(SepoliaBaseUrl), ManifestFilename);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _downloadDir.Dispose();
    }

    [Test]
    public async Task FetchManifest_WithSepoliaServer_ParsesAllEntries()
    {
        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await _client.FetchManifestAsync();

        manifest.Should().NotBeEmpty();
        manifest.Keys.Min().Should().Be(0, "epoch 0 must be the first entry");
        manifest.Keys.Should().OnlyContain(epoch => epoch >= 0);

        manifest.Should().ContainKey(Epoch0)
            .WhoseValue.Filename.Should().Be(Epoch0Filename,
                "epoch 0 filename is immutable — if this fails the server format has changed");

        manifest.Values.Should().OnlyContain(entry =>
            entry.Sha256Hash.Length == 32, "every entry must carry a full 32-byte SHA-256 hash");

        manifest.Values.Should().OnlyContain(entry =>
            entry.Filename.EndsWith(".erae"), "every entry filename must use the .erae extension");
    }

    [Test]
    public async Task DownloadEpochZero_WithSepoliaServer_PassesSha256Verification()
    {
        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await _client.FetchManifestAsync();
        RemoteEraEntry epoch0Entry = manifest[Epoch0];

        string destinationPath = Path.Join(_downloadDir.Path, epoch0Entry.Filename);
        await _client.DownloadFileAsync(epoch0Entry.Filename, destinationPath);

        File.Exists(destinationPath).Should().BeTrue();
        new FileInfo(destinationPath).Length.Should().BeGreaterThan(0);

        using FileStream fs = new(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] actualHash = SHA256.HashData(fs);
        actualHash.Should().Equal(epoch0Entry.Sha256Hash,
            "SHA-256 of the downloaded file must match the server manifest — file integrity is intact");
    }

    [Test]
    public async Task FindBlockAndReceipts_WithRealSepoliaEpochZero_ReturnsCorrectBlock()
    {
        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await _client.FetchManifestAsync();
        RemoteEraEntry epoch0Entry = manifest[Epoch0];

        // Pre-download so the decorator serves from cache (avoids double download in this test)
        string destinationPath = Path.Join(_downloadDir.Path, epoch0Entry.Filename);
        await _client.DownloadFileAsync(epoch0Entry.Filename, destinationPath);

        using RemoteEraStoreDecorator sut = new(
            localStore: null,
            _client,
            _downloadDir.Path,
            MaxEraSize);

        // Block 1 is the first non-genesis block — epoch 0 must contain it
        (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(1, ensureValidated: false);

        block.Should().NotBeNull();
        receipts.Should().NotBeNull();
        block!.Number.Should().Be(1);
        block.Hash.Should().NotBeNull("a real downloaded block must have a computed hash");
        block.ParentHash.Should().NotBeNull();
    }
}
