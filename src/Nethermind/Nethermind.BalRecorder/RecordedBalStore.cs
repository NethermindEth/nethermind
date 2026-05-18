// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

public class RecordedBalStore(IBalRecorderConfig config, IInitConfig initConfig, ILogManager logManager) : IRecordedBalStore
{
    private readonly ILogger _logger = logManager.GetClassLogger<RecordedBalStore>();
    private readonly SlotStore _store = new(
        config.Path.GetApplicationResourcePath(initConfig.BaseDbPath), "bal");

    public bool ReplayEnabled => config.ReplayEnabled;
    public bool RecordingEnabled => config.RecordingEnabled;

    public void Dispose() => _store.Dispose();

    public void Insert(Block block, BlockAccessList bal)
    {
        using NettyRlpStream rlp = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        if (!_store.Write(block.Number, rlp.AsSpan()))
            if (_logger.IsDebug) _logger.Debug($"BAL slot for block {block.Number} already filled; skipping.");
    }

    public BlockAccessList? Get(long blockNumber)
    {
        ReadState state = new() { Logger = _logger, BlockNumber = blockNumber };
        _store.TryRead(blockNumber, static (data, s) =>
        {
            try { s.Value = BlockAccessListDecoder.Instance.Decode(data); }
            catch (RlpException ex) { s.Logger.Warn($"Corrupt BAL slot for block {s.BlockNumber}: {ex.Message}"); }
        }, state);
        return state.Value;
    }

    private sealed class ReadState
    {
        public BlockAccessList? Value;
        public ILogger Logger;
        public long BlockNumber;
    }
}
