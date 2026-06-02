// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Shared envelope writer for streaming <c>eth_simulateV1</c> / <c>debug_simulateV1</c> /
/// <c>trace_simulateV1</c> responses. Owns the outer JSON array bracketing and the
/// failure-mapping (error message + error code) that the buffered executor performs
/// post-hoc on a built result list. The streaming pipeline has nowhere to hand a
/// <see cref="ResultWrapper{T}"/> failure object back to the JSON-RPC envelope after
/// the response has begun, so we serialize it inline as a trailing object inside the
/// outer array — clients distinguish payload blocks (have <c>"number"</c>) from
/// failures (have <c>"error"</c> + <c>"errorCode"</c>).
/// </summary>
internal static class SimulateEnvelopeWriter
{
    public static void EmitOuterArray(
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> emitBlocks)
    {
        Exception? failure = null;

        writer.WriteStartArray();
        try
        {
            emitBlocks(writer, pipeWriter, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            failure = ex;
        }
        finally
        {
            if (failure is not null)
            {
                WriteFailureObject(writer, FormatErrorMessage(failure), ResolveErrorCode(failure));
            }
            writer.WriteEndArray();
        }
    }

    public static void WriteFailureObject(Utf8JsonWriter writer, string message, int errorCode)
    {
        writer.WriteStartObject();
        writer.WriteString("error"u8, message);
        writer.WriteNumber("errorCode"u8, errorCode);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Mirrors <see cref="DebugModule.StructLogEnvelopeWriter.FormatErrorMessage"/> for symmetry
    /// across debug_*/trace_* streaming paths so HTTP clients see the same shape on failure.
    /// </summary>
    internal static string FormatErrorMessage(Exception failure) => failure switch
    {
        InvalidTransactionException tx => $"simulation failed: {tx.Reason.ErrorDescription}",
        _ => $"simulation failed: {failure.Message}",
    };

    internal static int ResolveErrorCode(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InsufficientBalanceException or InvalidBlockException or InvalidTransactionException) return ErrorCodes.InvalidInput;
        }

        return ErrorCodes.InternalError;
    }
}
