// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using DbMetrics = Nethermind.Db.Metrics;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

public class RecordedBalStore(string directory, IBalRecorderConfig config) : IRecordedBalStore
{
    private static readonly PrewarmerGetTimeLabel ReadLabel = new("bal_replay_read", isPrewarmer: true);
    private static readonly PrewarmerGetTimeLabel DecodeLabel = new("bal_decode", isPrewarmer: true);

    private readonly SlotStore _store = new(directory, "bal");

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
        ResultBox box = new();
        bool measureMetric = DbMetrics.DetailedMetricsEnabled;
        long swRead = Stopwatch.GetTimestamp();
        _store.TryRead(blockNumber, static (data, state) =>
        {
            long swDecode = Stopwatch.GetTimestamp();
            state.Result = BlockAccessListDecoder.Instance.Decode(data);
            if (state.MeasureMetric)
                DbMetrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - swDecode, DecodeLabel);
        }, box.With(measureMetric));
        if (measureMetric)
            DbMetrics.PrewarmerGetTime.Observe(Stopwatch.GetTimestamp() - swRead, ReadLabel);
        return box.Result;
    }

    private sealed class ResultBox
    {
        public BlockAccessList? Result;
        public bool MeasureMetric;
        public ResultBox With(bool measureMetric) { MeasureMetric = measureMetric; return this; }
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
