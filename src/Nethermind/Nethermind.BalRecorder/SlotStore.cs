// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace Nethermind.BalRecorder;

/// <summary>
/// Stores arbitrary byte blobs indexed by block number using a slot-file layout.
/// Caches a single open <see cref="SlotFile"/> at a time to bound memory usage.
/// </summary>
public class SlotStore(string directory, string extension = "bin") : IDisposable
{
    private SlotFile? _file;
    private ulong? _fileEra;
    private readonly Lock _lock = new();

    private string FilePath(ulong era) => Path.Combine(directory, $"{era:D8}.{extension}");

    public bool TryRead<TArg>(ulong blockNumber, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        ulong era = blockNumber / SlotFile.SlotsPerFile;
        int slot = (int)(blockNumber % SlotFile.SlotsPerFile);
        lock (_lock)
        {
            if (_fileEra != era)
            {
                string path = FilePath(era);
                if (!File.Exists(path)) return false;
                _file?.Dispose();
                _file = new SlotFile(path);
                _fileEra = era;
            }
            return _file!.TryRead(slot, action, arg);
        }
    }

    public bool Write(ulong blockNumber, ReadOnlySpan<byte> data)
    {
        ulong era = blockNumber / SlotFile.SlotsPerFile;
        int slot = (int)(blockNumber % SlotFile.SlotsPerFile);
        lock (_lock)
        {
            if (_fileEra != era)
            {
                _file?.Dispose();
                Directory.CreateDirectory(directory);
                _file = new SlotFile(FilePath(era));
                _fileEra = era;
            }
            return _file!.TryWrite(slot, data);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
            _fileEra = null;
        }
    }
}
