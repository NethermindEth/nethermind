// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.EraE.Archive;
using Nethermind.EraE.Exceptions;
using NonBlocking;

namespace Nethermind.EraE.Store;

/// <summary>
/// Decorates an <see cref="IEraStore"/> with transparent remote download support.
/// On a cache miss the required epoch file is fetched from the configured remote server,
/// SHA-256 verified, and cached locally. Subsequent accesses are served from disk.
/// The inner store (backed by pre-existing local files) is tried first; the remote
/// path is only taken when the inner store returns a miss.
/// </summary>
public sealed class RemoteEraStoreDecorator : IEraStore
{
    private readonly IEraStore? _localStore;
    private readonly IRemoteEraClient _client;
    private readonly string _downloadDir;
    private readonly int _maxEraSize;

    // Manifest fetched once on first remote access
    private IReadOnlyDictionary<int, RemoteEraEntry>? _manifest;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    // One semaphore per epoch prevents concurrent duplicate downloads
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _epochLocks = new();

    // Verified epoch paths — populated after successful SHA-256 check
    private readonly ConcurrentDictionary<int, string> _verifiedEpochs = new();

    public long FirstBlock => GetFirstBlockAsync().GetAwaiter().GetResult();
    public long LastBlock => GetLastBlockAsync().GetAwaiter().GetResult();

    public RemoteEraStoreDecorator(
        IEraStore? localStore,
        IRemoteEraClient client,
        string downloadDir,
        int maxEraSize)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDir);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEraSize, 0);

        _localStore = localStore;
        _client = client;
        _downloadDir = downloadDir;
        _maxEraSize = maxEraSize;
        Directory.CreateDirectory(downloadDir);
    }

    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(
        long number, bool ensureValidated = true, CancellationToken cancellation = default)
    {
        if (_localStore is not null)
        {
            (Block? b, TxReceipt[]? r) = await _localStore.FindBlockAndReceipts(number, ensureValidated, cancellation);
            if (b is not null) return (b, r);
        }

        int epoch = (int)(number / _maxEraSize);
        string localPath = await EnsureEpochAvailableAsync(epoch, cancellation);

        using EraReader reader = new(localPath);
        if (number > reader.LastBlock) return (null, null);
        (Block block, TxReceipt[] receipts) = await reader.GetBlockByNumber(number, cancellation);
        return (block, receipts);
    }

    public long NextEraStart(long blockNumber)
    {
        if (_localStore is not null)
        {
            try { return _localStore.NextEraStart(blockNumber); }
            catch (ArgumentOutOfRangeException) { /* epoch not in local store, fall through */ }
        }

        int epoch = (int)(blockNumber / _maxEraSize);
        string localPath = EnsureEpochAvailableAsync(epoch).GetAwaiter().GetResult();
        using EraReader reader = new(localPath);
        return reader.LastBlock + 1;
    }

    public void Dispose()
    {
        _localStore?.Dispose();
        _manifestLock.Dispose();
        foreach (SemaphoreSlim s in _epochLocks.Values) s.Dispose();
    }

    private async Task<long> GetFirstBlockAsync(CancellationToken cancellation = default)
    {
        if (_localStore is not null) return _localStore.FirstBlock;

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation);
        if (manifest.Count == 0) throw new EraException("Remote eraE manifest is empty.");

        // Exact: era epochs are aligned to maxEraSize boundaries, so first epoch always starts at epoch * maxEraSize.
        return (long)manifest.Keys.Min() * _maxEraSize;
    }

    private async Task<long> GetLastBlockAsync(CancellationToken cancellation = default)
    {
        if (_localStore is not null) return _localStore.LastBlock;

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation);
        if (manifest.Count == 0) throw new EraException("Remote eraE manifest is empty.");

        // Upper-bound estimate: avoids downloading the last (potentially huge) epoch file just for validation.
        // The actual last block may be slightly lower for a non-full final epoch.
        // FindBlockAndReceipts returns (null, null) when number > reader.LastBlock, so importers that
        // rely on this value (to=0 / auto mode) will stop naturally at the real end.
        return (long)(manifest.Keys.Max() + 1) * _maxEraSize - 1;
    }

    private async Task<IReadOnlyDictionary<int, RemoteEraEntry>> GetManifestAsync(CancellationToken cancellation = default)
    {
        if (_manifest is not null) return _manifest;

        await _manifestLock.WaitAsync(cancellation);
        try
        {
            if (_manifest is not null) return _manifest;
            _manifest = await _client.FetchManifestAsync(cancellation);
            return _manifest;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task<string> EnsureEpochAvailableAsync(int epoch, CancellationToken cancellation = default)
    {
        if (_verifiedEpochs.TryGetValue(epoch, out string? cached))
            return cached;

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation);
        if (!manifest.TryGetValue(epoch, out RemoteEraEntry entry))
            throw new EraException($"Epoch {epoch} is not available in the remote eraE manifest.");

        string destinationPath = Path.Join(_downloadDir, entry.Filename);

        SemaphoreSlim epochLock = _epochLocks.GetOrAdd(epoch, _ => new SemaphoreSlim(1, 1));
        await epochLock.WaitAsync(cancellation);
        try
        {
            // Re-check after acquiring lock (another thread may have finished)
            if (_verifiedEpochs.TryGetValue(epoch, out cached))
                return cached;

            if (!File.Exists(destinationPath))
                await _client.DownloadFileAsync(entry.Filename, destinationPath, cancellation);

            VerifySha256(destinationPath, entry.Sha256Hash);
            _verifiedEpochs.TryAdd(epoch, destinationPath);
            return destinationPath;
        }
        catch
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }
        finally
        {
            epochLock.Release();
        }
    }

    private static void VerifySha256(string filePath, byte[] expectedHash)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        byte[] actual = SHA256.HashData(fs);
        if (!actual.SequenceEqual(expectedHash))
            throw new EraVerificationException($"SHA-256 checksum mismatch for '{Path.GetFileName(filePath)}'.");
    }
}
