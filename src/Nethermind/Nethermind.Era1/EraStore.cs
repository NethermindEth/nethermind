// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;
using NonBlocking;

namespace Nethermind.Era1;
public class EraStore : IEraStore
{
    private readonly char[] _eraSeparator = ['-'];

    private readonly ISpecProvider _specProvider;
    private readonly IBlockValidator _blockValidator;
    private readonly ISet<ValueHash256>? _trustedAccumulators;

    private readonly Dictionary<long, string> _epochs;
    private readonly ValueHash256[] _checksums;

    // Probably should be persisted in the directory so that on restart we would not verify the epoch again.
    // But that is more relevant when we read directly from the directory
    private readonly ConcurrentDictionary<long, bool> _verifiedEpochs = new();

    /// <summary>
    /// Simple mechanism to limit opened file. Each epoch is sharded to _maxOpenFile. Whenever a reader is to be used
    /// it is removed from _openedReader unless there are no opened reader yet. When the reader is returned, the
    /// reader try to put itself back in _openedReader. If failed it try to remove and dispose current reader,
    /// otherwise, it try to dispose itself.
    /// Note: EraReader itself is concurrent safe, so there is probably a better way to do this.
    /// </summary>
    private readonly int _maxOpenFile;
    private readonly ConcurrentDictionary<int, (long, EraReader)> _openedReader = new();

    private bool _disposed = false;
    private readonly int _maxEraFile;

    private int LastEpoch { get; set; }
    private int FirstEpoch { get; set; } = int.MaxValue;

    private long? _firstBlock = null;
    public long FirstBlock
    {
        get
        {
            if (_firstBlock == null)
            {
                using EraRenter _ = RentReader(FirstEpoch, out EraReader smallestEraReader);
                _firstBlock = smallestEraReader.FirstBlock;
            }
            return _firstBlock.Value;
        }
    }

    private long? _lastBlock = null;
    private readonly int _verifyConcurrency;

    public long LastBlock
    {
        get
        {
            if (_lastBlock == null)
            {
                using EraRenter _ = RentReader(LastEpoch, out EraReader biggestEraReader);
                _lastBlock = biggestEraReader.LastBlock;
            }
            return _lastBlock.Value;
        }
    }

    public EraStore(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IFileSystem fileSystem,
        string networkName,
        int maxEraSize,
        ISet<ValueHash256>? trustedAcccumulators,
        string directory,
        int verifyConcurrency = 0
    )
    {
        _specProvider = specProvider;
        _blockValidator = blockValidator;
        _trustedAccumulators = trustedAcccumulators;
        _maxEraFile = maxEraSize;
        _maxOpenFile = Environment.ProcessorCount * 2;
        if (_verifyConcurrency == 0) _verifyConcurrency = Environment.ProcessorCount;
        _verifyConcurrency = verifyConcurrency;

        // Geth behaviour seems to be to always read the checksum and fail when its missing.
        _checksums = fileSystem.File.ReadAllLines(Path.Join(directory, EraExporter.ChecksumsFileName))
            .Select(static (chk) => new ValueHash256(chk))
            .ToArray();

        bool hasEraFile = false;
        _epochs = new();
        foreach (var file in EraPathUtils.GetAllEraFiles(directory, networkName, fileSystem))
        {
            string[] parts = Path.GetFileName(file).Split(_eraSeparator);
            int epoch;
            if (parts.Length != 3 || !int.TryParse(parts[1], out epoch) || epoch < 0)
                throw new ArgumentException($"Malformed Era1 file '{file}'.", file);
            _epochs[epoch] = file;
            hasEraFile = true;
            if (epoch > LastEpoch)
                LastEpoch = epoch;
            if (epoch < FirstEpoch)
                FirstEpoch = epoch;
        }

        if (!hasEraFile)
        {
            throw new EraException($"No relevant era files in directory {directory}");
        }
    }

    private long GetEpochNumber(long blockNumber)
    {
        // This seems to be the geth way of encoding blocks.
        long epochOffset = (blockNumber - FirstBlock) / _maxEraFile;
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
        if (!(_verifiedEpochs.TryGetValue(epoch, out bool verified) && verified))
        {
            Task checksumTask = Task.Run(() =>
            {
                ValueHash256 checksum = reader.CalculateChecksum();
                ValueHash256 expectedChecksum = _checksums[epoch - FirstEpoch];
                if (checksum != expectedChecksum)
                {
                    throw new EraVerificationException(
                        $"Checksum verification failed. Checksum: {checksum}, Expected: {expectedChecksum}");
                }
            });

            Task accumulatorTask = Task.Run(async () =>
            {
                var eraAccumulator = await reader.VerifyContent(_specProvider, _blockValidator, _verifyConcurrency, cancellation);
                if (_trustedAccumulators != null && !_trustedAccumulators.Contains(eraAccumulator))
                {
                    throw new EraVerificationException($"Unable to verify epoch {epoch}. Accumulator {eraAccumulator} not trusted");
                }
            });

            await Task.WhenAll(checksumTask, accumulatorTask);

            _verifiedEpochs.TryAdd(epoch, true);
        }
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

        long partOfEpoch = GetEpochNumber(number);
        if (!_epochs.ContainsKey(partOfEpoch))
            return (null, null);

        using EraRenter _ = RentReader(partOfEpoch, out EraReader reader);
        if (ensureValidated) await EnsureEpochVerified(partOfEpoch, reader, cancellation);
        (Block b, TxReceipt[] r) = await reader.GetBlockByNumber(number, cancellation);
        return (b, r);
    }

    private EraRenter RentReader(long epoch, out EraReader reader)
    {
        GuardMissingEpoch(epoch);

        int shardIdx = (int)(epoch % _maxOpenFile);
        if (_openedReader.TryRemove(shardIdx, out (long, EraReader) openedReader))
        {
            if (openedReader.Item1 == epoch)
            {
                reader = openedReader.Item2;
                return new EraRenter(this, reader, epoch);
            }

            if (!_openedReader.TryAdd(shardIdx, openedReader))
            {
                openedReader.Item2.Dispose();
            }
        }

        reader = GetReader(epoch);
        return new EraRenter(this, reader, epoch);
    }

    private void ReturnReader(long epoch, EraReader reader)
    {
        int shardIdx = (int)(epoch % _maxOpenFile);

        if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;

        // Something opened another reader of the same shard.

        // We try to remove and dispose the current one first.
        if (_openedReader.TryRemove(shardIdx, out (long, EraReader) existingReader))
        {
            existingReader.Item2.Dispose();
        }

        if (_openedReader.TryAdd(shardIdx, (epoch, reader))) return;

        // Still failed, so we just dispose ourself
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
            throw new ArgumentOutOfRangeException($"Epoch not available.", epoch, nameof(epoch));
    }

    private readonly struct EraRenter(EraStore store, EraReader reader, long epoch) : IDisposable
    {
        public void Dispose()
        {
            store.ReturnReader(epoch, reader);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (KeyValuePair<int, (long, EraReader)> kv in _openedReader)
        {
            kv.Value.Item2.Dispose();
        }
    }
}
