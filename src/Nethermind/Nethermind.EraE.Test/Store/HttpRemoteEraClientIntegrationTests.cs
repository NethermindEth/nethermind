// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.EraE.Store;
using Nethermind.Specs;
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
    private const uint Epoch0 = 0;
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
        IReadOnlyDictionary<uint, RemoteEraEntry> manifest = await _client.FetchManifestAsync();

        Assert.That(manifest, Is.Not.Empty);
        Assert.That(manifest.Keys.Min(), Is.EqualTo(0u), "epoch 0 must be the first entry");

        Assert.That(manifest.ContainsKey(Epoch0), Is.True);
        Assert.That(manifest[Epoch0].Filename, Is.EqualTo(Epoch0Filename), "epoch 0 filename is immutable — if this fails the server format has changed");

        Assert.That(manifest.Values.All(entry =>
            entry.Sha256Hash.Length == 32), Is.True, "every entry must carry a full 32-byte SHA-256 hash");

        Assert.That(manifest.Values.All(entry =>
            entry.Filename.EndsWith(".erae")), Is.True, "every entry filename must use the .erae extension");
    }

    [Test]
    public async Task DownloadEpochZero_WithSepoliaServer_PassesSha256Verification()
    {
        IReadOnlyDictionary<uint, RemoteEraEntry> manifest = await _client.FetchManifestAsync();
        RemoteEraEntry epoch0Entry = manifest[Epoch0];

        string destinationPath = Path.Join(_downloadDir.Path, epoch0Entry.Filename);
        await _client.DownloadFileAsync(epoch0Entry.Filename, destinationPath);

        Assert.That(File.Exists(destinationPath), Is.True);
        Assert.That(new FileInfo(destinationPath).Length, Is.GreaterThan(0));

        using FileStream fs = new(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] actualHash = SHA256.HashData(fs);
        Assert.That(actualHash, Is.EqualTo(epoch0Entry.Sha256Hash), "SHA-256 of the downloaded file must match the server manifest — file integrity is intact");
    }

    [Test]
    public async Task FindBlockAndReceipts_WithRealSepoliaEpochZero_ReturnsCorrectBlock()
    {
        IReadOnlyDictionary<uint, RemoteEraEntry> manifest = await _client.FetchManifestAsync();
        RemoteEraEntry epoch0Entry = manifest[Epoch0];

        // Pre-download so the decorator serves from cache (avoids double download in this test)
        string destinationPath = Path.Join(_downloadDir.Path, epoch0Entry.Filename);
        await _client.DownloadFileAsync(epoch0Entry.Filename, destinationPath);

        using RemoteEraStoreDecorator sut = new(
            localStore: null,
            _client,
            _downloadDir.Path,
            MaxEraSize,
            SepoliaSpecProvider.Instance,
            Always.Valid);

        // Block 1 is the first non-genesis block — epoch 0 must contain it
        (Block? block, TxReceipt[]? receipts) = await sut.FindBlockAndReceipts(1, ensureValidated: false);

        Assert.That(block, Is.Not.Null);
        Assert.That(receipts, Is.Not.Null);
        Assert.That(block!.Number, Is.EqualTo(1));
        Assert.That(block.Hash, Is.Not.Null, "a real downloaded block must have a computed hash");
        Assert.That(block.ParentHash, Is.Not.Null);
    }
}
