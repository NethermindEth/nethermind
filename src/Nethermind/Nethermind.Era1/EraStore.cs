// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;
using NonBlocking;

namespace Nethermind.Era1;
public class EraStore : IEraStore, IDisposable
{
    private TimeSpan ProgressInterval { get; } = TimeSpan.FromSeconds(10);
    private readonly char[] _eraSeparator = ['-'];
    private readonly Dictionary<long, string> _epochs;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Simple mechanism to limit opened file. Each epoch is sharded to _maxOpenFile. Whenever a reader is to be used
    /// it is removed from _openedReader unless there are no opened reader yet. When the reader is returned, the
    /// reader try to put itself back in _openedReader. If failed it try to remove and dispose current reader,
    /// otherwise, it try to dispose itself.
    /// Note: EraReader itself is concurrent safe, so there is probably a better way to do this.
    /// </summary>
    private readonly int _maxOpenFile;
    private readonly ConcurrentDictionary<int, EraReader> _openedReader = new();

    private bool _disposed = false;

    private int BiggestEpoch { get; set; }
    private int SmallestEpoch { get; set; }
    public long LastBlock { get; }

    public EraStore(string directory, string networkName, IFileSystem fileSystem)
    {
        _maxOpenFile = Environment.ProcessorCount * 2;

        var eraFiles = EraPathUtils.GetAllEraFiles(directory, networkName, fileSystem).ToArray();
        _epochs = new();
        foreach (var file in eraFiles)
        {
            string[] parts = Path.GetFileName(file).Split(_eraSeparator);
            int epoch;
            if (parts.Length != 3 || !int.TryParse(parts[1], out epoch) || epoch < 0)
                throw new ArgumentException($"Malformed Era1 file '{file}'.", nameof(eraFiles));
            _epochs[epoch] = file;
            if (epoch > BiggestEpoch)
                BiggestEpoch = epoch;
            if (epoch < SmallestEpoch)
                SmallestEpoch = epoch;
        }

        using EraRenter _ = RentReader(BiggestEpoch, out EraReader biggestEraReader);
        LastBlock = biggestEraReader.LastBlock;

        _fileSystem = fileSystem;
    }

    public bool HasEpoch(long epoch) => _epochs.ContainsKey(epoch);

    public EraReader GetReader(long epoch)
    {
        GuardMissingEpoch(epoch);
        return new EraReader(new E2StoreReader(_epochs[epoch]));
    }

    public async Task<Block?> FindBlock(long blockNumber, CancellationToken cancellation = default)
    {
        ThrowIfNegative(blockNumber);

        long partOfEpoch = blockNumber == 0 ? 0 : blockNumber / EraWriter.MaxEra1Size;
        if (!_epochs.ContainsKey(partOfEpoch))
            return null;

        using EraRenter _r = RentReader(partOfEpoch, out EraReader reader);
        (Block b, _) = await reader.GetBlockByNumber(blockNumber, cancellation);
        return b;
    }

    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, CancellationToken cancellation = default)
    {
        ThrowIfNegative(number);

        long partOfEpoch = number == 0 ? 0 : number / EraWriter.MaxEra1Size;
        if (!_epochs.ContainsKey(partOfEpoch))
            return (null, null);

        using EraRenter _ = RentReader(partOfEpoch, out EraReader reader);
        (Block b, TxReceipt[] r) = await reader.GetBlockByNumber(number, cancellation);
        return (b, r);
    }

    private EraRenter RentReader(long epoch, out EraReader reader)
    {
        GuardMissingEpoch(epoch);

        int shardIdx = (int)(epoch % _maxOpenFile);
        if (_openedReader.TryRemove(shardIdx, out reader!)) return new EraRenter(this, reader, shardIdx);

        reader = GetReader(epoch);
        // TODO: Verify here
        return new EraRenter(this, reader, shardIdx);
    }

    private void ReturnReader(EraReader reader, int shardIdx)
    {
        if (_openedReader.TryAdd(shardIdx, reader)) return;

        // Something opened another reader of the same shard.

        // We try to remove and dispose the current one first.
        if (_openedReader.TryRemove(shardIdx, out EraReader? existingReader))
        {
            existingReader.Dispose();
        }

        if (_openedReader.TryAdd(shardIdx, reader)) return;

        // Still failed, so we just dispose ourself
        reader.Dispose();
    }

    public async Task VerifyAll(ISpecProvider specProvider, CancellationToken cancellationToken, HashSet<ValueHash256>? trustedAccumulators = null, Action<VerificationProgressArgs>? onProgress = null)
    {
        if (trustedAccumulators != null)
        {
            // Must it? Like, what if there is less in the directory?
            if (_epochs.Count != trustedAccumulators.Count) throw new ArgumentException("Must have an equal amount of files and accumulators.", nameof(trustedAccumulators));
        }

        DateTime startTime = DateTime.Now;
        DateTime lastProgress = DateTime.Now;
        int fileCount = 0;
        foreach (KeyValuePair<long, string> kv in _epochs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string era = kv.Value;
            using MemoryStream destination = new();
            using EraReader eraReader = GetReader(kv.Key);
            var eraAccumulator = await eraReader.ReadAndVerifyAccumulator(specProvider, cancellationToken);
            if (trustedAccumulators != null && !trustedAccumulators.Contains(eraAccumulator))
            {
                throw new EraVerificationException($"Accumulator {eraAccumulator} not trusted from era file {era}");
            }

            fileCount++;
            TimeSpan elapsed = DateTime.Now.Subtract(lastProgress);
            if (elapsed.TotalSeconds > ProgressInterval.TotalSeconds)
            {
                onProgress?.Invoke(new VerificationProgressArgs(fileCount, _epochs.Count, DateTime.Now.Subtract(startTime)));
                lastProgress = DateTime.Now;
            }
        }
    }

    public async Task CreateAccumulatorFile(string accumulatorPath, CancellationToken cancellationToken)
    {
        _fileSystem.File.Delete(accumulatorPath);
        using StreamWriter stream = new StreamWriter(_fileSystem.File.Create(accumulatorPath), System.Text.Encoding.UTF8);
        bool first = true;

        foreach (var kv in _epochs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (EraReader reader = GetReader(kv.Key))
            {
                string root = (reader.ReadAccumulator()).BytesAsSpan.ToHexString(true);
                if (!first)
                    root = Environment.NewLine + root;
                else
                    first = false;
                await stream.WriteAsync(root);
            }
        }
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
            throw new ArgumentOutOfRangeException($"Does not contain epoch.", epoch, nameof(epoch));
    }

    private readonly struct EraRenter(EraStore store, EraReader reader, int shardIdx) : IDisposable
    {
        public void Dispose()
        {
            store.ReturnReader(reader, shardIdx);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (KeyValuePair<int, EraReader> kv in _openedReader)
        {
            kv.Value.Dispose();
        }
    }
}
