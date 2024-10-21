// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript.Util.Web;
using Nethermind.Blockchain.Era1;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Era1;

namespace Nethermind.Blockchain;
public class EraStore : IEraStore
{
    private readonly char[] _eraSeparator = ['-'];
    private readonly Dictionary<long, string> _epochs;
    private readonly IFileSystem _fileSystem;

    public int EpochCount => _epochs.Count;
    public int BiggestEpoch { get; private set; }
    public int SmallestEpoch { get; private set; }
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(10);

    public EraStore(string directory, string networkName, IFileSystem fileSystem)
    {
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
        _fileSystem = fileSystem;
    }

    public bool HasEpoch(long epoch) => _epochs.ContainsKey(epoch);

    public EraReader GetReader(long epoch)
    {
        GuardMissingEpoch(epoch);
        return new EraReader(new E2StoreReader(_epochs[epoch]));
    }

    public string GetReaderPath(long epoch)
    {
        GuardMissingEpoch(epoch);
        return _epochs[epoch];
    }

    public async Task<Block?> FindBlock(long number, CancellationToken cancellation = default)
    {
        ThrowIfNegative(number);

        long partOfEpoch = number == 0 ? 0 : number / EraWriter.MaxEra1Size;
        if (!_epochs.ContainsKey(partOfEpoch))
            return null;

        using EraReader reader = GetReader(partOfEpoch);
        (Block b, _) = await reader.GetBlockByNumber(number, cancellation);
        return b;
    }
    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, CancellationToken cancellation = default)
    {
        ThrowIfNegative(number);

        long partOfEpoch = number == 0 ? 0 : number / EraWriter.MaxEra1Size;
        if (!_epochs.ContainsKey(partOfEpoch))
            return (null, null);

        using EraReader reader = GetReader(partOfEpoch);
        (Block b, TxReceipt[] r) = await reader.GetBlockByNumber(number, cancellation);
        return (b, r);
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
            var eraAccumulator = eraReader.ReadAccumulator();
            if (trustedAccumulators != null && !trustedAccumulators.Contains(eraAccumulator))
            {
                throw new EraVerificationException($"Accumulator {eraAccumulator} not trusted from era file {era}");
            }

            if (!await eraReader.VerifyAccumulator(eraAccumulator, specProvider))
            {
                throw new EraVerificationException($"Failed to verify accumulator {eraAccumulator} from era file {era}");
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
}
