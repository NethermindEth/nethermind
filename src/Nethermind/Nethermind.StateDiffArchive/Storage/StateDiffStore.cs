// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiffArchive.Data;
using Snappier;

namespace Nethermind.StateDiffArchive.Storage;

/// <summary>
/// Persists and reads per-block state diffs: a <see cref="StateDiffRecordBuilder"/> is RLP-encoded,
/// Snappy-compressed, and stored as one blob keyed by block number in the era-file <see cref="SlotStore"/>;
/// reads decompress into a pooled buffer wrapped by a <see cref="StateDiffRecord"/> view.
/// </summary>
public sealed class StateDiffStore : IDisposable
{
    private const string FileExtension = "diff";

    private readonly SlotStore _store;
    private readonly ILogger _logger;

    public StateDiffStore(IStateDiffArchiveConfig config, IInitConfig initConfig, ILogManager logManager)
    {
        string directory = config.ArchivePath.GetApplicationResourcePath(initConfig.BaseDbPath);
        _store = new SlotStore(directory, FileExtension);
        _logger = logManager.GetClassLogger<StateDiffStore>();
        RecordingEnabled = config.RecordingEnabled;
        ReplayEnabled = config.ReplayEnabled;
        Directory = directory;
    }

    public bool RecordingEnabled { get; }
    public bool ReplayEnabled { get; }
    public string Directory { get; }

    public void Write(StateDiffRecordBuilder builder, ulong blockNumber, Hash256 stateRoot)
    {
        int length = builder.GetLength(blockNumber, stateRoot);
        byte[] rlp = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RlpWriter writer = new(rlp);
            builder.WriteTo(ref writer, blockNumber, stateRoot);
            byte[] compressed = Snappy.CompressToArray(rlp.AsSpan(0, length));
            // Overwrite so a canonical reorg replaces a previously-written (possibly orphaned) block.
            _store.Write(blockNumber, compressed, allowOverwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rlp);
        }
    }

    /// <returns>A pooled-buffer-backed view that the caller must dispose to return the buffer.</returns>
    public bool TryRead(ulong blockNumber, [NotNullWhen(true)] out StateDiffRecord? record)
    {
        ReadState state = new() { Logger = _logger, BlockNumber = blockNumber };
        _store.TryRead(blockNumber, static (compressed, s) =>
        {
            try
            {
                int length = Snappy.GetUncompressedLength(compressed);
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
                bool transferred = false;
                try
                {
                    Snappy.Decompress(compressed, owner.Memory.Span);
                    s.Record = new StateDiffRecord(owner, owner.Memory[..length]);
                    transferred = true; // the record now owns the buffer and returns it on Dispose
                }
                finally
                {
                    if (!transferred) owner.Dispose();
                }
            }
            catch (Exception ex) when (ex is RlpException or InvalidOperationException or ArgumentException)
            {
                if (s.Logger.IsWarn) s.Logger.Warn($"Corrupt state-diff record for block {s.BlockNumber}: {ex.Message}");
            }
        }, state);

        record = state.Record;
        return record is not null;
    }

    public void Dispose() => _store.Dispose();

    private sealed class ReadState
    {
        public StateDiffRecord? Record;
        public ILogger Logger;
        public ulong BlockNumber;
    }
}
