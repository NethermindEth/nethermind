// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

/// <summary>
/// Tracer that can store traces of decorated tracer in database
/// </summary>
/// <typeparam name="TTrace">Trace type</typeparam>
/// <typeparam name="TTracer">Transaction tracer type</typeparam>
public class DbPersistingBlockTracer<TTrace, TTracer> : IBlockTracer where TTracer : class, ITxTracer
{
    private readonly IDb _db;
    private readonly ITraceSerializer<TTrace> _traceSerializer;
    private readonly IBlockTracer _blockTracer;
    private readonly BlockTracerBase<TTrace, TTracer> _tracerWithResults;
    private Keccak _currentBlockHash = null!;
    private long _currentBlockNumber;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the tracer
    /// </summary>
    /// <param name="blockTracer">Internal, actual tracer that does the tracing</param>
    /// <param name="db">Database</param>
    /// <param name="traceSerializer">Serializer</param>
    /// <param name="logManager"></param>
    public DbPersistingBlockTracer(BlockTracerBase<TTrace, TTracer> blockTracer,
        IDb db,
        ITraceSerializer<TTrace> traceSerializer,
        ILogManager logManager)
    {
        _db = db;
        _traceSerializer = traceSerializer;
        _blockTracer = _tracerWithResults = blockTracer;
        _logger = logManager.GetClassLogger<DbPersistingBlockTracer<TTrace, TTracer>>();
    }

    public bool IsTracingRewards => _blockTracer.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _blockTracer.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        _currentBlockHash = block.Hash!;
        _currentBlockNumber = block.Number;
        _blockTracer.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => _blockTracer.StartNewTxTrace(tx);

    public void EndTxTrace() => _blockTracer.EndTxTrace();

    public void EndBlockTrace()
    {
        _blockTracer.EndBlockTrace();
        IReadOnlyCollection<TTrace> result = _tracerWithResults.BuildResult();
        Keccak currentBlockHash = _currentBlockHash;
        long currentBlockNumber = _currentBlockNumber;
        try
        {
            byte[] tracesSerialized = _traceSerializer.Serialize(result);
            _db.Set(currentBlockHash, tracesSerialized);
            if (_logger.IsTrace) _logger.Trace($"Saved traces for block {currentBlockNumber} ({currentBlockHash}) with size {tracesSerialized.Length} bytes for {result.Count} traces.");
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Couldn't save traces for block {currentBlockNumber} ({currentBlockHash}), {ex.Message}");
        }
    }
}
