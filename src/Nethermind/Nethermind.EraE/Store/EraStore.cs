// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Archive;
using Nethermind.EraE.E2Store;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;

namespace Nethermind.EraE.Store;

public sealed class EraStore : IEraStore
{
    private static readonly char[] _eraSeparator = ['-'];

    private readonly ISpecProvider _specProvider;
    private readonly IBlockValidator _blockValidator;
    private readonly ISet<ValueHash256>? _trustedAccumulators;
    private readonly Proofs.Validator? _validator;

    private readonly Dictionary<long, string> _epochs;
    private readonly Dictionary<long, ValueHash256> _checksumsByEpoch = new();

    private readonly ConcurrentDictionary<long, bool> _verifiedEpochs = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _epochLocks = new();

    private readonly int _maxOpenFile;
    private readonly ConcurrentDictionary<int, (long Epoch, EraReader Reader)> _openedReader = new();

    private readonly int _maxEraSize;
    private readonly int _verifyConcurrency;
    private volatile bool _disposed;

    private readonly Lazy<(long First, long Last)> _blockRange;

    public (long First, long Last) BlockRange => _blockRange.Value;

    private int LastEpoch { get; }
    private int FirstEpoch { get; } = int.MaxValue;

    public EraStore(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IFileSystem fileSystem,
        string networkName,
        int maxEraSize,
        ISet<ValueHash256>? trustedAccumulators,
        string directory,
        int verifyConcurrency = 0,
        Proofs.Validator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(blockValidator);
        ArgumentException.ThrowIfNullOrEmpty(networkName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEraSize, 0);

        _specProvider = specProvider;
        _blockValidator = blockValidator;
        _trustedAccumulators = trustedAccumulators;
        _validator = validator;
        _maxEraSize = maxEraSize;
        _maxOpenFile = Environment.ProcessorCount * 2;
        _verifyConcurrency = verifyConcurrency == 0 ? Environment.ProcessorCount : verifyConcurrency;

        foreach (string line in fileSystem.File.ReadAllLines(Path.Join(directory, EraExporter.ChecksumsSHA256FileName)))
        {
            (long checksumEpoch, ValueHash256 hash) = EraPathUtils.ParseChecksumEntry(line);
            _checksumsByEpoch[checksumEpoch] = hash;
        }

        bool hasEraFile = false;
        _epochs = [];

        foreach (string file in EraPathUtils.GetAllEraFiles(directory, networkName, fileSystem))
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split(_eraSeparator);
            if (parts.Length != 3 || !int.TryParse(parts[1], out int epoch) || epoch < 0)
                throw new ArgumentException($"Malformed EraE file '{file}'.", file);

            _epochs[epoch] = file;
            hasEraFile = true;
            if (epoch > LastEpoch) LastEpoch = epoch;
            if (epoch < FirstEpoch) FirstEpoch = epoch;
        }

        if (!hasEraFile)
            throw new EraException($"No relevant erae files in directory {directory}.");

        _blockRange = new Lazy<(long, long)>(() =>
        {
            using EraRenter f = RentReader(FirstEpoch, out EraReader firstReader);
            using EraRenter l = RentReader(LastEpoch, out EraReader lastReader);
            return (firstReader.FirstBlock, lastReader.LastBlock);
        });
    }

    private long GetEpochNumber(long blockNumber) => blockNumber / _maxEraSize;

    private EraReader GetReader(long epoch) => !_epochs.TryGetValue(epoch, out string? path)
        ? throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "Epoch not available.")
        : new EraReader(new E2StoreReader(path));

    private async ValueTask EnsureEpochVerified(long epoch, EraReader reader, CancellationToken cancellation)
    {
        if (_verifiedEpochs.TryGetValue(epoch, out bool verified) && verified) return;

        SemaphoreSlim epochLock = _epochLocks.GetOrAddDisposable(epoch, static _ => new SemaphoreSlim(1, 1));
        await epochLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Double-check: another worker may have verified this epoch while we were waiting
            if (_verifiedEpochs.TryGetValue(epoch, out verified) && verified) return;

            // Both tasks share the same EraReader. E2StoreReader uses RandomAccess (positional I/O)
            // so concurrent reads from different offsets are safe without additional locking.
            Task checksumTask = Task.Run(() =>
            {
                ValueHash256 actual = reader.CalculateChecksum();
                if (!_checksumsByEpoch.TryGetValue(epoch, out ValueHash256 expected))
                    throw new EraVerificationException($"No checksum entry found for epoch {epoch}.");
                if (actual != expected)
                    throw new EraVerificationException($"Checksum mismatch for epoch {epoch}. Got {actual}, expected {expected}.");
            }, cancellation);

            Task accumulatorTask = Task.Run(async () =>
            {
                ValueHash256 accRoot = await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, _validator, cancellation).ConfigureAwait(false);
                if (_trustedAccumulators != null && accRoot != default && !_trustedAccumulators.Contains(accRoot))
                    throw new EraVerificationException($"AccumulatorRoot {accRoot} for epoch {epoch} is not trusted.");
            }, cancellation);

            await Task.WhenAll(checksumTask, accumulatorTask).ConfigureAwait(false);
            _verifiedEpochs.TryAdd(epoch, true);
        }
        finally
        {
            epochLock.Release();
        }
    }

    public bool HasEpoch(long blockNumber) => _epochs.ContainsKey(GetEpochNumber(blockNumber));

    public long NextEraStart(long blockNumber)
    {
        long epoch = GetEpochNumber(blockNumber);
        using EraRenter _ = RentReader(epoch, out EraReader reader);
        return reader.LastBlock + 1;
    }

    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, bool ensureValidated = true, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);

        long epoch = GetEpochNumber(number);
        if (!_epochs.ContainsKey(epoch))
            return (null, null);

        using EraRenter _ = RentReader(epoch, out EraReader reader);
        if (ensureValidated) await EnsureEpochVerified(epoch, reader, cancellation);
        (Block b, TxReceipt[] r) = await reader.GetBlockByNumber(number, cancellation);
        return (b, r);
    }

    private EraRenter RentReader(long epoch, out EraReader reader)
    {
        int shardIdx = (int)(epoch % _maxOpenFile);
        if (_openedReader.TryRemove(shardIdx, out (long Epoch, EraReader Reader) opened))
        {
            if (opened.Epoch == epoch)
            {
                reader = opened.Reader;
                return new EraRenter(this, reader, epoch);
            }

            if (!_openedReader.TryAdd(shardIdx, opened))
                opened.Reader.Dispose();
        }

        reader = GetReader(epoch);
        return new EraRenter(this, reader, epoch);
    }

    private void ReturnReader(long epoch, EraReader reader)
    {
        if (!_disposed)
        {
            int shardIdx = (int)(epoch % _maxOpenFile);

            if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;

            if (_openedReader.TryRemove(shardIdx, out (long Epoch, EraReader Reader) existing))
                existing.Reader.Dispose();

            if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;
        }

        reader.Dispose();
    }

    private readonly struct EraRenter(EraStore store, EraReader reader, long epoch) : IDisposable
    {
        public void Dispose() => store.ReturnReader(epoch, reader);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (KeyValuePair<int, (long Epoch, EraReader Reader)> kv in _openedReader)
            kv.Value.Reader.Dispose();

        foreach (KeyValuePair<long, SemaphoreSlim> kv in _epochLocks)
            kv.Value.Dispose();
    }
}
