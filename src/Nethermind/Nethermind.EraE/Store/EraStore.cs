// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Archive;
using Nethermind.EraE.E2Store;
using Nethermind.EraE.Exceptions;
using Nethermind.EraE.Export;
using NonBlocking;

namespace Nethermind.EraE.Store;

public sealed class EraStore : IEraStore
{
    private readonly char[] _eraSeparator = ['-'];

    private readonly ISpecProvider _specProvider;
    private readonly IBlockValidator _blockValidator;
    private readonly ISet<ValueHash256>? _trustedAccumulators;

    private readonly Dictionary<long, string> _epochs;
    private readonly ValueHash256[] _checksums;

    private readonly ConcurrentDictionary<long, bool> _verifiedEpochs = new();

    private readonly int _maxOpenFile;
    private readonly ConcurrentDictionary<int, (long Epoch, EraReader Reader)> _openedReader = new();

    private readonly int _maxEraSize;
    private readonly int _verifyConcurrency;
    private bool _disposed;

    private long? _firstBlock;
    private long? _lastBlock;

    public long FirstBlock
    {
        get
        {
            if (_firstBlock is null)
            {
                using EraRenter _ = RentReader(FirstEpoch, out EraReader reader);
                _firstBlock = reader.FirstBlock;
            }
            return _firstBlock.Value;
        }
    }

    public long LastBlock
    {
        get
        {
            if (_lastBlock is null)
            {
                using EraRenter _ = RentReader(LastEpoch, out EraReader reader);
                _lastBlock = reader.LastBlock;
            }
            return _lastBlock.Value;
        }
    }

    private int LastEpoch { get; set; }
    private int FirstEpoch { get; set; } = int.MaxValue;

    public EraStore(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IFileSystem fileSystem,
        string networkName,
        int maxEraSize,
        ISet<ValueHash256>? trustedAccumulators,
        string directory,
        int verifyConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(blockValidator);
        ArgumentException.ThrowIfNullOrEmpty(networkName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEraSize, 0);

        _specProvider = specProvider;
        _blockValidator = blockValidator;
        _trustedAccumulators = trustedAccumulators;
        _maxEraSize = maxEraSize;
        _maxOpenFile = Environment.ProcessorCount * 2;
        _verifyConcurrency = verifyConcurrency == 0 ? Environment.ProcessorCount : verifyConcurrency;

        _checksums = fileSystem.File.ReadAllLines(Path.Join(directory, EraExporter.ChecksumsFileName))
            .Select(static chk => EraPathUtils.ExtractHashFromChecksumEntry(chk))
            .ToArray();

        bool hasEraFile = false;
        _epochs = new Dictionary<long, string>();

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
    }

    private long GetEpochNumber(long blockNumber)
    {
        long epochOffset = (blockNumber - FirstBlock) / _maxEraSize;
        return FirstEpoch + epochOffset;
    }

    private bool HasEpoch(long epoch) => _epochs.ContainsKey(epoch);

    private EraReader GetReader(long epoch)
    {
        GuardMissingEpoch(epoch);
        return new EraReader(new E2StoreReader(_epochs[epoch]));
    }

    private async ValueTask EnsureEpochVerified(long epoch, EraReader reader, CancellationToken cancellation)
    {
        if (_verifiedEpochs.TryGetValue(epoch, out bool verified) && verified) return;

        Task checksumTask = Task.Run(() =>
        {
            ValueHash256 actual = reader.CalculateChecksum();
            ValueHash256 expected = _checksums[epoch - FirstEpoch];
            if (actual != expected)
                throw new EraVerificationException($"Checksum mismatch for epoch {epoch}. Got {actual}, expected {expected}.");
        });

        Task accumulatorTask = Task.Run(async () =>
        {
            ValueHash256 accRoot = await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, cancellation);
            if (_trustedAccumulators != null && accRoot != default && !_trustedAccumulators.Contains(accRoot))
                throw new EraVerificationException($"AccumulatorRoot {accRoot} for epoch {epoch} is not trusted.");
        });

        await Task.WhenAll(checksumTask, accumulatorTask);
        _verifiedEpochs.TryAdd(epoch, true);
    }

    public long NextEraStart(long blockNumber)
    {
        long epoch = GetEpochNumber(blockNumber);
        using EraRenter _ = RentReader(epoch, out EraReader reader);
        return reader.LastBlock + 1;
    }

    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, bool ensureValidated = true, CancellationToken cancellation = default)
    {
        ThrowIfNegative(number);

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
        GuardMissingEpoch(epoch);

        int shardIdx = (int)(epoch % _maxOpenFile);
        if (_openedReader.TryRemove(shardIdx, out (long, EraReader) opened))
        {
            if (opened.Item1 == epoch)
            {
                reader = opened.Item2;
                return new EraRenter(this, reader, epoch);
            }

            if (!_openedReader.TryAdd(shardIdx, opened))
                opened.Item2.Dispose();
        }

        reader = GetReader(epoch);
        return new EraRenter(this, reader, epoch);
    }

    private void ReturnReader(long epoch, EraReader reader)
    {
        int shardIdx = (int)(epoch % _maxOpenFile);

        if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;

        if (_openedReader.TryRemove(shardIdx, out (long, EraReader) existing))
            existing.Item2.Dispose();

        if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;

        reader.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfNegative(long number)
    {
        if (number < 0)
            throw new ArgumentOutOfRangeException(nameof(number), number, "Cannot be negative.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GuardMissingEpoch(long epoch)
    {
        if (!HasEpoch(epoch))
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "Epoch not available.");
    }

    private readonly struct EraRenter(EraStore store, EraReader reader, long epoch) : IDisposable
    {
        public void Dispose() => store.ReturnReader(epoch, reader);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (KeyValuePair<int, (long, EraReader)> kv in _openedReader)
            kv.Value.Item2.Dispose();
    }
}
