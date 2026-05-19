// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streaming result for <c>debug_traceCallMany</c> (no overrides).
/// <see cref="Count"/> is always 0; iteration is empty.
/// </summary>
[JsonConverter(typeof(GethLikeTxTraceStreamingBundleResultConverter))]
public sealed class GethLikeTxTraceStreamingBundleResult : StreamingResultBase, IEnumerable<IEnumerable<GethLikeTxTrace>>
{
    private readonly IDebugBridge _bridge;
    private readonly TransactionBundle[] _bundles;
    private readonly BlockParameter _blockParameter;
    private readonly long? _gasCap;
    private readonly GethTraceOptions _options;

    public GethLikeTxTraceStreamingBundleResult(
        IDebugBridge bridge,
        TransactionBundle[] bundles,
        BlockParameter blockParameter,
        long? gasCap,
        GethTraceOptions options,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts, logger)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(bundles);
        ArgumentNullException.ThrowIfNull(blockParameter);
        ArgumentNullException.ThrowIfNull(options);

        _bridge = bridge;
        _bundles = bundles;
        _blockParameter = blockParameter;
        _gasCap = gasCap;
        _options = options;
    }

    public int Count => 0;
    public IEnumerator<IEnumerable<GethLikeTxTrace>> GetEnumerator() => Enumerable.Empty<IEnumerable<GethLikeTxTrace>>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        writer.WriteStartArray();
        try
        {
            foreach (TransactionBundle bundle in _bundles)
            {
                EmitBundle(writer, pipeWriter, cancellationToken, bundle);
            }
        }
        finally
        {
            writer.WriteEndArray();
        }
    }

    private void EmitBundle(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken, TransactionBundle bundle)
    {
        writer.WriteStartArray();
        try
        {
            foreach (TransactionForRpc txForRpc in bundle.Transactions)
            {
                EmitTraceForTx(writer, pipeWriter, cancellationToken, txForRpc);
            }
        }
        finally
        {
            writer.WriteEndArray();
        }

        FlushBetweenBundles(writer, pipeWriter, cancellationToken);
    }

    private void EmitTraceForTx(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken, TransactionForRpc txForRpc)
    {
        Result<Transaction> txResult = txForRpc.ToTransaction(validateUserInput: true, gasCap: _gasCap);
        if (!txResult.Success(out Transaction? tx, out string? validationError))
        {
            StructLogEnvelopeWriter.EmitFailedTrace(writer, txForRpc.Gas ?? 0L, validationError);
            return;
        }

        StructLogEnvelopeWriter.EmitTraceObject(writer, pipeWriter, cancellationToken,
            (w, pw, t) => _bridge.GetTransactionTraceStreaming(tx, _blockParameter, w, pw, t, _options),
            Logger,
            fallbackGas: tx.GasLimit);
    }

    private static void FlushBetweenBundles(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        if (pipeWriter is null) return;
        writer.Flush();
        pipeWriter.FlushAsync(cancellationToken).GetAwaiter().GetResult();
    }
}

internal sealed class GethLikeTxTraceStreamingBundleResultConverter : JsonConverter<GethLikeTxTraceStreamingBundleResult>
{
    public override GethLikeTxTraceStreamingBundleResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethLikeTxTraceStreamingBundleResult? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteAsJson(writer);
    }
}
