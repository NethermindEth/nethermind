// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;

namespace Nethermind.BalRecorder;

/// <summary>
/// Stores arbitrary byte blobs indexed by block number, using an era file layout.
/// One <see cref="SlotFile"/> covers <see cref="SlotFile.SlotsPerFile"/> consecutive blocks.
/// Files are opened lazily and kept open for the store's lifetime.
/// Non-existent files are re-checked on every read miss (no null caching).
/// </summary>
public class SlotStore(string directory, string extension = "bin") : IDisposable
{
    private readonly ConcurrentDictionary<long, Lazy<SlotFile>> _files = new();

    private string FilePath(long era) => Path.Combine(directory, $"{era:D8}.{extension}");

    private Lazy<SlotFile>? TryGetOrOpenForRead(long era)
    {
        if (_files.TryGetValue(era, out Lazy<SlotFile>? existing)) return existing;
        string path = FilePath(era);
        if (!File.Exists(path)) return null;
        return _files.GetOrAdd(era, static (_, p) => new Lazy<SlotFile>(() => new SlotFile(p)), path);
    }

    private Lazy<SlotFile> GetOrCreateForWrite(long era)
    {
        Directory.CreateDirectory(directory);
        return _files.GetOrAdd(era, static (e, ctx) =>
            new Lazy<SlotFile>(() => new SlotFile(ctx.path)),
            (path: FilePath(era), _: 0));
    }

    public bool TryRead<TArg>(long blockNumber, ReadOnlySpanAction<byte, TArg> action, TArg arg)
    {
        long era = blockNumber / SlotFile.SlotsPerFile;
        Lazy<SlotFile>? file = TryGetOrOpenForRead(era);
        return file?.Value.TryRead((int)(blockNumber % SlotFile.SlotsPerFile), action, arg) ?? false;
    }

    public void Write(long blockNumber, ReadOnlySpan<byte> data)
    {
        long era = blockNumber / SlotFile.SlotsPerFile;
        GetOrCreateForWrite(era).Value.Write((int)(blockNumber % SlotFile.SlotsPerFile), data);
    }

    public void Dispose()
    {
        foreach (Lazy<SlotFile> lazy in _files.Values)
        {
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        }
        _files.Clear();
    }
}
