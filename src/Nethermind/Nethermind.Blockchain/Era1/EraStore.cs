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
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Era1;

namespace Nethermind.Blockchain;
public class EraStore: IEraStore
{
    private readonly char[] _fileNameSeparator = ['-'];
    private readonly Dictionary<long, string> _epochs;
    private readonly IFileSystem _fileSystem;

    public int EpochCount => _epochs.Count;
    public int BiggestEpoch { get; private set; }
    public int SmallestEpoch { get; private set; }

    public EraStore(string[] eraFiles) : this(eraFiles, new FileSystem()) { }
    public EraStore(string[] eraFiles, IFileSystem fileSystem)
    {
        _epochs = new();
        foreach (var file in eraFiles)
        {
            string[] parts = Path.GetFileName(file).Split(_fileNameSeparator);
            int epoch;
            if (parts.Length != 3 || !int.TryParse(parts[1], out epoch) || epoch < 0)
                throw new ArgumentException($"Malformed Era1 file '{file}'.",nameof(eraFiles));
            _epochs[epoch] = file;
            if (epoch > BiggestEpoch)
                BiggestEpoch = epoch;
            if (epoch < SmallestEpoch)
                SmallestEpoch = epoch;
        }
        _fileSystem = fileSystem;
    }
    public bool HasEpoch(long epoch) => _epochs.ContainsKey(epoch);
    
    public Task<EraReader> GetReader(long epoch, bool descendingOrder, CancellationToken cancellation = default)
    {
        GuardMissingEpoch(epoch);
        return EraReader.Create(_epochs[epoch], descendingOrder, cancellation);
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
        using (EraReader reader = await EraReader.Create(_fileSystem.File.OpenRead(_epochs[partOfEpoch]), cancellation))
        {
            (Block b, _, _) = await reader.GetBlockByNumber(number, cancellation);
            return b;
        }
    }
    public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(long number, CancellationToken cancellation = default)
    {
        ThrowIfNegative(number);

        long partOfEpoch = number == 0 ? 0 : number / EraWriter.MaxEra1Size;
        if (!_epochs.ContainsKey(partOfEpoch))
            return (null, null);
        using (EraReader reader = await EraReader.Create(_fileSystem.File.OpenRead(_epochs[partOfEpoch]), cancellation))
        {
            (Block b, TxReceipt[] r, _) = await reader.GetBlockByNumber(number, cancellation);
            return (b, r);
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
