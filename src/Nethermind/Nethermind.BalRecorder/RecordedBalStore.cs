// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

public class RecordedBalStore(IBalRecorderConfig config, IInitConfig initConfig, ILogManager logManager) : IRecordedBalStore
{
    private static readonly BlockAccessListDecoder BalDecoder = BlockAccessListDecoder.Instance;
    private readonly ILogger _logger = logManager.GetClassLogger<RecordedBalStore>();
    private readonly SlotStore _store = new(
        config.Path.GetApplicationResourcePath(initConfig.BaseDbPath), "bal");

    public bool ReplayEnabled => config.ReplayEnabled;
    public bool RecordingEnabled => config.RecordingEnabled;

    public void Dispose() => _store.Dispose();

    public void Insert(Block block, GeneratedBlockAccessList bal)
    {
        using ArrayPoolSpan<byte> rlp = BlockAccessListDecoder.EncodeToArrayPoolSpan(bal);
        if (!_store.Write(block.Number, rlp))
            if (_logger.IsDebug) _logger.Debug($"BAL slot for block {block.Number} already filled; skipping.");
    }

    public ReadOnlyBlockAccessList? Get(long blockNumber)
    {
        ReadState state = new() { Logger = _logger, BlockNumber = blockNumber };
        _store.TryRead(blockNumber, static (data, s) =>
        {
            try { s.Value = BalDecoder.Decode(data); }
            catch (RlpException ex) { s.Logger.Warn($"Corrupt BAL slot for block {s.BlockNumber}: {ex.Message}"); }
        }, state);
        return state.Value;
    }

    private sealed class ReadState
    {
        public ReadOnlyBlockAccessList? Value;
        public ILogger Logger;
        public long BlockNumber;
    }
}
