// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
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
        _store.Write(block.Number, rlp.AsSpan());
    }

    public BlockAccessList? Get(long blockNumber, Hash256 blockHash)
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

public class NullRecordedBalStore : IRecordedBalStore
{
    public static NullRecordedBalStore Instance { get; } = new();
    public void Insert(Block block, BlockAccessList bal) { }
    public BlockAccessList? Get(long blockNumber, Hash256 blockHash) => null;
    public bool ReplayEnabled => false;
    public bool RecordingEnabled => false;
    public void Dispose() { }
}
