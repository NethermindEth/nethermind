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
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streaming block-trace result. Traces are written straight to the response;
/// <see cref="Count"/> is always 0 and <see cref="GetEnumerator"/> is always empty.
/// </summary>
[JsonConverter(typeof(GethLikeTxTraceStreamingBlockResultConverter))]
public sealed class GethLikeTxTraceStreamingBlockResult : JsonStreamingResultBase, IReadOnlyCollection<GethLikeTxTrace>
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runBlockTrace;

    public GethLikeTxTraceStreamingBlockResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runBlockTrace,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts, logger)
    {
        ArgumentNullException.ThrowIfNull(runBlockTrace);
        _runBlockTrace = runBlockTrace;
    }

    public int Count => 0;
    public IEnumerator<GethLikeTxTrace> GetEnumerator() => Enumerable.Empty<GethLikeTxTrace>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        bool completeArray = true;
        bool deadlineExpired = false;

        writer.WriteStartArray();

        try
        {
            _runBlockTrace(writer, pipeWriter, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (ex is GethTraceDeadlineException)
            {
                if (pipeWriter is not StagingPipeWriter { IsCommitted: true })
                {
                    completeArray = false;
                    throw;
                }
                deadlineExpired = true;
                CommittedFailure = ex;
            }
            failure = ex;
        }
        finally
        {
            if (completeArray)
            {
                if (failure is not null)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("error"u8);
                    writer.WriteStringValue(deadlineExpired ? "execution timeout" : StructLogEnvelopeWriter.FormatErrorMessage(failure));
                    writer.WritePropertyName("errorCode"u8);
                    writer.WriteNumberValue(deadlineExpired ? ErrorCodes.InvalidInput : StructLogEnvelopeWriter.ResolveErrorCode(failure));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
        }

        if (failure is not null && !deadlineExpired && Logger.IsWarn)
        {
            Logger.Warn($"debug_traceBlock streaming failed mid-response: {failure}");
        }
    }
}

internal sealed class GethLikeTxTraceStreamingBlockResultConverter : JsonConverter<GethLikeTxTraceStreamingBlockResult>
{
    public override GethLikeTxTraceStreamingBlockResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethLikeTxTraceStreamingBlockResult? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteAsJson(writer);
    }
}
