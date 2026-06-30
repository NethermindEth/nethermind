// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace Nethermind.StateDiffArchive.Storage;

/// <summary>
/// Stores arbitrary byte blobs indexed by block number across a directory of era files
/// (<c>{era:D8}.{extension}</c>, where <c>era = blockNumber / <see cref="SlotFile.SlotsPerFile"/></c>).
/// Caches a single open <see cref="SlotFile"/> at a time to bound memory and file-handle usage.
/// </summary>
/// <remarks>
/// Sequential block access (the recorder's append pattern and the replayer's forward walk) touches one
/// era file at a time, so the single-file cache is near-optimal. Not safe for concurrent access from
/// multiple block numbers in different eras; the recorder and replayer are single-threaded on the main
/// processing path.
/// </remarks>
public sealed class SlotStore(string directory, string extension = "bin") : IDisposable
{
    private SlotFile? _file;
    private ulong? _fileEra;
    private readonly Lock _lock = new();

    public string Directory => directory;
    public string Extension => extension;

    public static string FilePath(string directory, ulong era, string extension) => Path.Combine(directory, $"{era:D8}.{extension}");

    private string FilePath(ulong era) => FilePath(directory, era, extension);

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

    public bool Write(ulong blockNumber, ReadOnlySpan<byte> data, bool allowOverwrite = false)
    {
        ulong era = blockNumber / SlotFile.SlotsPerFile;
        int slot = (int)(blockNumber % SlotFile.SlotsPerFile);
        lock (_lock)
        {
            if (_fileEra != era)
            {
                _file?.Dispose();
                System.IO.Directory.CreateDirectory(directory);
                _file = new SlotFile(FilePath(era));
                _fileEra = era;
            }
            return _file!.TryWrite(slot, data, allowOverwrite);
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
