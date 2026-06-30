// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Api;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiffArchive.Data;
using Snappier;

namespace Nethermind.StateDiffArchive.Storage;

/// <summary>
/// Persists and reads per-block <see cref="StateDiffRecord"/>s: each record is RLP-encoded, Snappy-compressed,
/// and stored as one blob keyed by block number in the era-file <see cref="SlotStore"/>.
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
        VerifyStateRoot = config.VerifyStateRoot;
        Directory = directory;
    }

    public bool RecordingEnabled { get; }
    public bool ReplayEnabled { get; }
    public bool VerifyStateRoot { get; }
    public string Directory { get; }

    public void Write(StateDiffRecord record)
    {
        int length = StateDiffRecordDecoder.Instance.GetLength(record);
        byte[] rlp = new byte[length];
        RlpWriter writer = new(rlp);
        StateDiffRecordDecoder.Instance.Encode(ref writer, record);

        byte[] compressed = Snappy.CompressToArray(rlp);
        // Overwrite so a canonical reorg replaces a previously-written (possibly orphaned) block.
        _store.Write(record.BlockNumber, compressed, allowOverwrite: true);
    }

    public bool TryRead(ulong blockNumber, [NotNullWhen(true)] out StateDiffRecord? record)
    {
        ReadState state = new() { Logger = _logger, BlockNumber = blockNumber };
        _store.TryRead(blockNumber, static (compressed, s) =>
        {
            try
            {
                byte[] rlp = Snappy.DecompressToArray(compressed);
                RlpReader reader = new(rlp);
                s.Record = StateDiffRecordDecoder.Instance.Decode(ref reader);
            }
            catch (Exception ex) when (ex is RlpException or InvalidOperationException)
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
