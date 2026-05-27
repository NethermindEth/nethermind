// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.EraE.Archive;
using EraException = Nethermind.Era1.Exceptions.EraException;
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
    private readonly ISpecProvider _specProvider;
    private readonly IBlockValidator _blockValidator;
    private readonly Proofs.Validator? _validator;
    private readonly ISet<ValueHash256>? _trustedAccumulators;
    private readonly int _verifyConcurrency;
    // Bounded reader pool: capped at ProcessorCount*2 to stay within OS fd limits.
    // Mainnet has ~1600 epochs; Linux default fd limit is 1024 — an unbounded pool would exhaust it.
    private readonly int _maxOpenReaders;
    private volatile bool _disposed;

    // Manifest fetched once on first remote access
    private IReadOnlyDictionary<int, RemoteEraEntry>? _manifest;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    // One semaphore per epoch prevents concurrent duplicate downloads.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _epochLocks = new();

    // Available epoch paths — populated after successful download + SHA-256 check
    private readonly ConcurrentDictionary<int, string> _availableEpochs = new();

    // Epochs whose content has passed EraReader.VerifyContent (only required when ensureValidated)
    private readonly ConcurrentDictionary<int, bool> _contentVerifiedEpochs = new();

    // Bounded idle reader pool — readers are checked out (TryRemove) before use and returned
    // (TryAdd) when done, so the eviction path can only dispose readers that are not in flight.
    private readonly ConcurrentDictionary<int, EraReader> _openedReaders = new();

    // Setup path — sequential, sync-over-async is safe (see thread-safety model above)
    public (long First, long Last) BlockRange => GetBlockRangeAsync().GetAwaiter().GetResult();

    public RemoteEraStoreDecorator(
        IEraStore? localStore,
        IRemoteEraClient client,
        string downloadDir,
        int maxEraSize,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        ISet<ValueHash256>? trustedAccumulators = null,
        int verifyConcurrency = 0,
        Nethermind.EraE.Proofs.Validator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDir);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEraSize, 0);
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(blockValidator);

        _localStore = localStore;
        _client = client;
        _downloadDir = downloadDir;
        _maxEraSize = maxEraSize;
        _specProvider = specProvider;
        _blockValidator = blockValidator;
        _trustedAccumulators = trustedAccumulators;
        _verifyConcurrency = verifyConcurrency;
        _validator = validator;
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
        string localPath = await EnsureEpochAvailableAsync(epoch, ensureValidated, cancellation).ConfigureAwait(false);

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
        string localPath = EnsureEpochAvailableAsync(epoch, ensureValidated: false).GetAwaiter().GetResult();
        using EraReader reader = new(localPath);
        return reader.LastBlock + 1;
    }

    public void Dispose()
    {
        _disposed = true;
        _localStore?.Dispose();
        _manifestLock.Dispose();
        foreach (KeyValuePair<int, SemaphoreSlim> kvp in _epochLocks)
            kvp.Value.Dispose();
        foreach (KeyValuePair<int, EraReader> kvp in _openedReaders)
            kvp.Value.Dispose();
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
            foreach (KeyValuePair<int, EraReader> kvp in _openedReaders)
                if (kvp.Key < oldest) oldest = kvp.Key;
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

    private async Task<string> EnsureEpochAvailableAsync(int epoch, bool ensureValidated, CancellationToken cancellation = default)
    {
        if (_availableEpochs.TryGetValue(epoch, out string? cached)
            && (!ensureValidated || _contentVerifiedEpochs.ContainsKey(epoch)))
            return cached;

        IReadOnlyDictionary<int, RemoteEraEntry> manifest = await GetManifestAsync(cancellation).ConfigureAwait(false);
        if (!manifest.TryGetValue(epoch, out RemoteEraEntry entry))
            throw new EraException($"Epoch {epoch} is not available in the remote eraE manifest.");

        string destinationPath = ResolveDestinationPath(entry.Filename);

        SemaphoreSlim epochLock = _epochLocks.GetOrAddDisposable(epoch, static _ => new SemaphoreSlim(1, 1));
        await epochLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring lock (another thread may have finished)
            if (_availableEpochs.TryGetValue(epoch, out cached)
                && (!ensureValidated || _contentVerifiedEpochs.ContainsKey(epoch)))
                return cached;

            if (!_availableEpochs.ContainsKey(epoch))
            {
                if (!File.Exists(destinationPath))
                    await _client.DownloadFileAsync(entry.Filename, destinationPath, cancellation).ConfigureAwait(false);

                VerifySha256(destinationPath, entry.Sha256Hash);
                _availableEpochs.TryAdd(epoch, destinationPath);
            }

            if (ensureValidated)
            {
                await VerifyEpochContentAsync(epoch, destinationPath, cancellation).ConfigureAwait(false);
                _contentVerifiedEpochs.TryAdd(epoch, true);
            }

            return destinationPath;
        }
        catch (OperationCanceledException)
        {
            if (!_availableEpochs.ContainsKey(epoch) && File.Exists(destinationPath))
                File.Delete(destinationPath);
            _contentVerifiedEpochs.TryRemove(epoch, out _);
            throw;
        }
        catch (Exception)
        {
            // A failed download, checksum, or content check leaves no trusted state behind.
            _availableEpochs.TryRemove(epoch, out _);
            _contentVerifiedEpochs.TryRemove(epoch, out _);
            if (_openedReaders.TryRemove(epoch, out EraReader? staleReader))
                staleReader.Dispose();
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }
        finally
        {
            epochLock.Release();
        }
    }

    private async Task VerifyEpochContentAsync(int epoch, string localPath, CancellationToken cancellation)
    {
        using EraReader reader = new(localPath);
        ValueHash256 accumulatorRoot =
            await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, _validator, cancellation).ConfigureAwait(false);
        if (_trustedAccumulators is not null && accumulatorRoot != default && !_trustedAccumulators.Contains(accumulatorRoot))
            throw new EraVerificationException($"AccumulatorRoot {accumulatorRoot} for epoch {epoch} is not trusted.");
    }

    private string ResolveDestinationPath(string filename)
    {
        string root = Path.GetFullPath(_downloadDir);
        string destinationPath = Path.GetFullPath(Path.Join(root, filename));
        string boundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(boundary, StringComparison.Ordinal))
            throw new EraException($"Remote eraE manifest filename '{filename}' escapes the download directory.");

        return destinationPath;
    }

    private static void VerifySha256(string filePath, byte[] expectedHash)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        byte[] actual = SHA256.HashData(fs);
        if (!actual.AsSpan().SequenceEqual(expectedHash))
            throw new EraVerificationException($"SHA-256 checksum mismatch for '{Path.GetFileName(filePath)}'.");
    }
}
