// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.EraE.Archive;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;

namespace Nethermind.EraE.Store;

// Thread-safety model:
//   Setup paths  (BlockRange, NextEraStart) — called sequentially during initialization,
//                never from an async context; sync-over-async via GetAwaiter().GetResult() is safe.
//   Read paths   (FindBlockAndReceipts) — called concurrently by multiple importer worker tasks;
//                protected by per-epoch semaphores and a manifest lock.
public sealed class RemoteEraStoreDecorator : IEraStore
{
    private readonly IEraStore? _localStore;
    private readonly IRemoteEraClient _client;
    private readonly string _downloadDir;
    private readonly int _maxEraSize;
    // Bounded reader pool: capped at ProcessorCount*2 to stay within OS fd limits.
    // Mainnet has ~1600 epochs; Linux default fd limit is 1024 — an unbounded pool would exhaust it.
    private readonly int _maxOpenReaders;
    private volatile bool _disposed;

    // Manifest fetched once on first remote access
    private IReadOnlyDictionary<int, RemoteEraEntry>? _manifest;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    // One semaphore per epoch prevents concurrent duplicate downloads.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _epochLocks = new();

    // Verified epoch paths — populated after successful SHA-256 check
    private readonly ConcurrentDictionary<int, string> _verifiedEpochs = new();

    // Bounded idle reader pool — readers are checked out (TryRemove) before use and returned
    // (TryAdd) when done, so the eviction path can only dispose readers that are not in flight.
    private readonly ConcurrentDictionary<int, EraReader> _openedReaders = new();

    // Setup path — sequential, sync-over-async is safe (see thread-safety model above)
    public (long First, long Last) BlockRange => GetBlockRangeAsync().GetAwaiter().GetResult();

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
        _maxOpenReaders = Math.Max(Environment.ProcessorCount * 2, 8);
        Directory.CreateDirectory(downloadDir);
    }

    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(
        long number, bool ensureValidated = true, CancellationToken cancellation = default)
    {
        if (_localStore is not null)
        {
            (Block? b, TxReceipt[]? r) = await _localStore.FindBlockAndReceipts(number, ensureValidated, cancellation).ConfigureAwait(false);
            if (b is not null) return (b, r);
        }

        int epoch = (int)(number / _maxEraSize);
        string localPath = await EnsureEpochAvailableAsync(epoch, cancellation).ConfigureAwait(false);

        using EraRenter renter = RentReader(epoch, localPath);
        if (number > renter.Reader.LastBlock) return (null, null);
        (Block block, TxReceipt[] receipts) = await renter.Reader.GetBlockByNumber(number, cancellation).ConfigureAwait(false);
        return (block, receipts);
    }

    public bool HasEpoch(long blockNumber) => _localStore is not null && _localStore.HasEpoch(blockNumber);

    public long NextEraStart(long blockNumber)
    {
        if (_localStore is not null && _localStore.HasEpoch(blockNumber))
            return _localStore.NextEraStart(blockNumber);

        int epoch = (int)(blockNumber / _maxEraSize);
        // Setup path — sequential, sync-over-async is safe (see thread-safety model above)
        string localPath = EnsureEpochAvailableAsync(epoch).GetAwaiter().GetResult();
        using EraReader reader = new(localPath);
        return reader.LastBlock + 1;
    }

    public void Dispose()
    {
        _disposed = true;
        _localStore?.Dispose();
        _manifestLock.Dispose();
        foreach (SemaphoreSlim s in _epochLocks.Values)
            s.Dispose();
        foreach (EraReader reader in _openedReaders.Values)
            reader.Dispose();
    }

    private EraRenter RentReader(int epoch, string localPath)
    {
        // Fast path: check out an existing idle reader.
        if (_openedReaders.TryRemove(epoch, out EraReader? existing))
            return new EraRenter(this, existing, epoch);

        // Evict the oldest (lowest-epoch) idle reader when the pool is at capacity.
        // Import accesses epochs in ascending order, so the lowest epoch is always the
        // least-recently-used and is safe to close.
        if (_openedReaders.Count >= _maxOpenReaders)
        {
            int oldest = int.MaxValue;
            foreach (int key in _openedReaders.Keys)
                if (key < oldest) oldest = key;
            if (_openedReaders.TryRemove(oldest, out EraReader? evicted))
                evicted.Dispose();
        }

        return new EraRenter(this, new EraReader(localPath), epoch);
    }

    private void ReturnReader(int epoch, EraReader reader)
    {
        if (_disposed || !_openedReaders.TryAdd(epoch, reader))
            reader.Dispose();
    }

    private readonly struct EraRenter(RemoteEraStoreDecorator store, EraReader reader, int epoch) : IDisposable
    {
        public EraReader Reader => reader;
        public void Dispose() => store.ReturnReader(epoch, reader);
    }

    private async Task<(long First, long Last)> GetBlockRangeAsync(CancellationToken cancellation = default)
    {
        if (_localStore is not null) return _localStore.BlockRange;

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation).ConfigureAwait(false);
        if (manifest.Count == 0) throw new EraException("Remote eraE manifest is empty.");

        (int minEpoch, int maxEpoch) = manifest.Keys.MinMax();

        // First is exact: era epochs are aligned to maxEraSize boundaries.
        // Last is upper-bound estimate: avoids downloading the last (potentially huge) epoch file just for validation.
        // The actual last block may be slightly lower for a non-full final epoch.
        // FindBlockAndReceipts returns (null, null) when number > reader.LastBlock, so importers that
        // rely on this value (to=0 / auto mode) will stop naturally at the real end.
        return ((long)minEpoch * _maxEraSize, (long)(maxEpoch + 1) * _maxEraSize - 1);
    }

    private async Task<IReadOnlyDictionary<int, RemoteEraEntry>> GetManifestAsync(CancellationToken cancellation = default)
    {
        if (_manifest is not null) return _manifest;

        await _manifestLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_manifest is not null) return _manifest;
            _manifest = await _client.FetchManifestAsync(cancellation).ConfigureAwait(false);
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

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation).ConfigureAwait(false);
        if (!manifest.TryGetValue(epoch, out RemoteEraEntry entry))
            throw new EraException($"Epoch {epoch} is not available in the remote eraE manifest.");

        string destinationPath = Path.Join(_downloadDir, entry.Filename);

        SemaphoreSlim epochLock = _epochLocks.GetOrAddDisposable(epoch, static _ => new SemaphoreSlim(1, 1));
        await epochLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring lock (another thread may have finished)
            if (_verifiedEpochs.TryGetValue(epoch, out cached))
                return cached;

            if (!File.Exists(destinationPath))
                await _client.DownloadFileAsync(entry.Filename, destinationPath, cancellation).ConfigureAwait(false);

            VerifySha256(destinationPath, entry.Sha256Hash);
            _verifiedEpochs.TryAdd(epoch, destinationPath);
            return destinationPath;
        }
        catch (Exception)
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
        if (!actual.AsSpan().SequenceEqual(expectedHash))
            throw new EraVerificationException($"SHA-256 checksum mismatch for '{Path.GetFileName(filePath)}'.");
    }
}
