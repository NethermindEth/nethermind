// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.Tracing;

internal sealed class GethLikeBlockMuxTracer : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    internal const string TracerName = "muxTracer";

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        MaxDepth = EthereumJsonSerializer.DefaultMaxDepth,
        TypeInfoResolver = TracingJsonContext.Default
    };

    private readonly Hash256? _txHash;
    private readonly bool _collectCompletedTransactions;
    private int _completedTransactions;
    private bool _tracingTransaction;
    private readonly CompositeBlockTracer _composite = new();
    private readonly Dictionary<string, IBlockTracer<GethLikeTxTrace>> _children = [];
    private readonly List<Hash256?> _transactions = [];
    private IReadOnlyCollection<GethLikeTxTrace>? _results;
    private bool _releasedResults;

    internal GethLikeBlockMuxTracer(GethTraceOptions options, Func<GethTraceOptions, IBlockTracer<GethLikeTxTrace>> createChild)
    {
        _txHash = options.TxHash;
        _collectCompletedTransactions = options.CollectCompletedTransactions;
        try
        {
            foreach ((string name, JsonElement config) in ParseConfig(options.TracerConfig))
            {
                if (name.Length == 0)
                    throw new InvalidDataException("SyntaxError: SyntaxError: (anonymous): Line 1:3 Unexpected token )");
                IBlockTracer<GethLikeTxTrace> child = createChild(options with { Tracer = name, TracerConfig = config });
                _children.Add(name, child);
                _composite.Add(child);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static Dictionary<string, JsonElement> ParseConfig(JsonElement? config)
    {
        if (config is null || config.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            string type = config.Value.ValueKind switch
            {
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                _ => "bool"
            };
            throw new InvalidDataException($"json: cannot unmarshal {type} into Go value of type map[string]jsontext.Value");
        }
        return TypeInfoJsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.Value, ResultOptions)!;
    }

    /// <inheritdoc/>
    public bool IsTracingRewards => _composite.IsTracingRewards;

    /// <inheritdoc/>
    public void StartNewBlockTrace(Block block)
    {
        ReleaseResults();
        _results = null;
        _releasedResults = false;
        _transactions.Clear();
        _completedTransactions = 0;
        _tracingTransaction = false;
        _composite.StartNewBlockTrace(block);
    }

    /// <inheritdoc/>
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _tracingTransaction = tx is not null && (_txHash is null || _txHash == tx.Hash);
        if (_tracingTransaction)
            _transactions.Add(tx!.Hash);
        List<ITxTracer> started = new(_children.Count);
        try
        {
            foreach (IBlockTracer<GethLikeTxTrace> child in _children.Values)
            {
                ITxTracer tracer = child.StartNewTxTrace(tx);
                if (tracer != NullTxTracer.Instance) started.Add(tracer);
            }
            return started.Count switch
            {
                0 => NullTxTracer.Instance,
                1 => started[0],
                _ => new CompositeTxTracer(started)
            };
        }
        catch (Exception exception)
        {
            foreach (ITxTracer tracer in started) tracer.Dispose();
            if (!_collectCompletedTransactions) Dispose();
            if (exception is ArgumentException or JsonException or FileNotFoundException)
                throw new InvalidDataException(exception.Message, exception);
            throw;
        }
    }

    /// <inheritdoc/>
    public void EndTxTrace()
    {
        _composite.EndTxTrace();
        if (_tracingTransaction) _completedTransactions++;
        _tracingTransaction = false;
    }
    /// <inheritdoc/>
    public void EndBlockTrace() => _composite.EndBlockTrace();
    /// <inheritdoc/>
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _composite.ReportReward(author, rewardType, rewardValue);

    /// <inheritdoc/>
    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        if (_results is not null) return _results;
        int count = _collectCompletedTransactions ? _completedTransactions : _transactions.Count;
        Dictionary<string, JsonElement>[] values = new Dictionary<string, JsonElement>[count];
        GethLikeTxTrace[] results = new GethLikeTxTrace[count];
        for (int i = 0; i < results.Length; i++)
        {
            values[i] = new Dictionary<string, JsonElement>(_children.Count);
            results[i] = new GethLikeTxTrace
            {
                TxHash = _transactions[i],
                CustomTracerResult = new GethLikeCustomTrace { Value = values[i] }
            };
        }
        try
        {
            foreach ((string name, IBlockTracer<GethLikeTxTrace> child) in _children)
            {
                int index = 0;
                foreach (GethLikeTxTrace trace in child.BuildResult())
                {
                    if (!_collectCompletedTransactions || index < results.Length)
                        values[index].Add(name, JsonSerializer.SerializeToElement(trace,
                            TypeInfoJsonSerializer.GetTypeInfo<GethLikeTxTrace>(EthereumJsonSerializer.JsonOptions)));
                    index++;
                }
                if (index < results.Length || index > _transactions.Count)
                    throw new InvalidOperationException("Mux child returned a different number of transaction traces.");
            }
            for (int i = 0; i < results.Length; i++)
                results[i].CustomTracerResult!.Value = JsonSerializer.SerializeToElement(values[i],
                    TypeInfoJsonSerializer.GetTypeInfo<Dictionary<string, JsonElement>>(ResultOptions));
            return _results = results;
        }
        finally
        {
            // Child results may own pooled memory; keep only their serialized values after aggregation.
            ReleaseResults();
        }
    }

    private void ReleaseResults()
    {
        if (_releasedResults) return;
        _releasedResults = true;
        foreach (IBlockTracer<GethLikeTxTrace> child in _children.Values)
        {
            if (child is GethLikeBlockMuxTracer) continue;
            foreach (GethLikeTxTrace trace in child.BuildResult())
                trace.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (IBlockTracer<GethLikeTxTrace> child in _children.Values)
            child.TryDispose();
        ReleaseResults();
    }
}
