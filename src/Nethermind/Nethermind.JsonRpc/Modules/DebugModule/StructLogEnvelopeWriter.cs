// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Shared envelope writer for streaming Geth-style struct-log traces. Single-tx and
/// bundle (debug_traceCallMany) results both use this to keep the per-tx output format
/// byte-identical and the failure-handling path DRY.
/// </summary>
internal static class StructLogEnvelopeWriter
{
    public static void EmitTraceObject(
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        Func<Utf8JsonWriter, PipeWriter?, CancellationToken, GethLikeTxTrace?> runTrace,
        ILogger logger,
        long fallbackGas = 0L)
    {
        GethLikeTxTrace? trace = null;
        Exception? failure = null;

        writer.WriteStartObject();
        writer.WritePropertyName("structLogs"u8);
        writer.WriteStartArray();

        try
        {
            trace = runTrace(writer, pipeWriter, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            failure = ex;
        }
        finally
        {
            writer.WriteEndArray();
            string? errorMessage = failure is null ? null : $"tracing failed: {failure.Message}";
            int errorCode = ResolveErrorCode(failure);
            WriteFooter(writer, trace, errorMessage, errorCode, fallbackGas);
        }

        LogFailure(logger, failure);
    }

    /// <summary>
    /// Writes a failure trace envelope for the case where the trace was never invoked
    /// (e.g. tx couldn't even be constructed from RPC input). No structLogs are emitted.
    /// </summary>
    public static void EmitFailedTrace(Utf8JsonWriter writer, long gasLimit, string? errorMessage = null)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("structLogs"u8);
        writer.WriteStartArray();
        writer.WriteEndArray();
        WriteFooter(writer, trace: null, errorMessage, ErrorCodes.InvalidInput, fallbackGas: gasLimit);
    }

    private static int ResolveErrorCode(Exception? failure)
    {
        if (failure is null) return ErrorCodes.InvalidInput;

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InsufficientBalanceException or InvalidBlockException) return ErrorCodes.InvalidInput;
        }

        return ErrorCodes.InternalError;
    }

    private static void WriteFooter(Utf8JsonWriter writer, GethLikeTxTrace? trace, string? errorMessage, int errorCode, long fallbackGas)
    {
        long gas = trace?.Gas ?? fallbackGas;
        bool failed = errorMessage is not null || trace is null || trace.Failed;
        byte[] returnValue = trace?.ReturnValue ?? [];

        ForcedNumberConversion.WriteRawLong(writer, "gas"u8, gas);

        writer.WritePropertyName("failed"u8);
        writer.WriteBooleanValue(failed);

        writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(writer, returnValue, EthereumJsonSerializer.JsonOptions);

        if (errorMessage is not null)
        {
            writer.WritePropertyName("error"u8);
            writer.WriteStringValue(errorMessage);

            writer.WritePropertyName("errorCode"u8);
            writer.WriteNumberValue(errorCode);
        }

        writer.WriteEndObject();
    }

    private static void LogFailure(ILogger logger, Exception? failure)
    {
        if (failure is null) return;
        if (logger.IsWarn) logger.Warn($"debug_trace streaming failed mid-response: {failure}");
    }
}
