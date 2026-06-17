// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using ErrorType = Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType;

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
            string? errorMessage;
            int errorCode;
            if (failure is not null)
            {
                errorMessage = FormatErrorMessage(failure);
                errorCode = ResolveErrorCode(failure);
            }
            else if (trace is null)
            {
                errorMessage = "tracing failed: trace not found";
                errorCode = ErrorCodes.ResourceNotFound;
            }
            else
            {
                errorMessage = null;
                errorCode = default;
            }
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
        string? formattedMessage = errorMessage is null ? null : $"tracing failed: {errorMessage}";
        WriteFooter(writer, trace: null, formattedMessage, ErrorCodes.InvalidInput, fallbackGas: gasLimit);
    }

    internal static int ResolveErrorCode(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InsufficientBalanceException or InvalidBlockException or InvalidTransactionException) return ErrorCodes.InvalidInput;
        }

        return ErrorCodes.InternalError;
    }

    internal static string FormatErrorMessage(Exception failure) => failure switch
    {
        InvalidTransactionException tx => $"tracing failed: {FormatErrorDescription(tx.Reason)}",
        _ => $"tracing failed: {failure.Message}",
    };

    private static string FormatErrorDescription(TransactionResult result)
    {
        string detail = result.ErrorDescription;
        return result.Error switch
        {
            ErrorType.InsufficientSenderBalance => ReplacePrefix(detail, "insufficient sender balance for transfer", "insufficient funds for transfer"),
            ErrorType.InsufficientMaxFeePerGasForSenderBalance => detail,
            ErrorType.SenderHasDeployedCode => ReplacePrefix(detail, "sender has deployed code", "sender not an eoa"),
            ErrorType.NonceOverflow => ReplacePrefix(detail, "nonce overflow", "nonce has max value"),
            ErrorType.MinerPremiumNegative => ReplacePrefix(detail, "miner premium is negative", "max priority fee per gas higher than max fee per gas"),
            ErrorType.TransactionSizeOverMaxInitCodeSize => ReplacePrefix(detail, "EIP-3860 - transaction size over max init code size", "max initcode size exceeded"),
            ErrorType.BlockGasLimitExceeded => ReplacePrefix(detail, "Block gas limit exceeded", "exceeds block gas limit"),
            _ => detail,
        };
    }

    private static string ReplacePrefix(string s, string oldPrefix, string newPrefix)
        => s.StartsWith(oldPrefix, StringComparison.Ordinal) ? newPrefix + s[oldPrefix.Length..] : s;

    private static void WriteFooter(Utf8JsonWriter writer, GethLikeTxTrace? trace, string? errorMessage, int errorCode, long fallbackGas)
    {
        long gas = trace?.Gas ?? fallbackGas;
        bool failed = errorMessage is not null || trace is null || trace.Failed;
        byte[] returnValue = trace?.ReturnValue ?? [];

        writer.WriteNumber("gas"u8, gas);

        writer.WritePropertyName("failed"u8);
        writer.WriteBooleanValue(failed);

        writer.WritePropertyName("returnValue"u8);
        ByteArrayConverter.Convert(writer, returnValue, skipLeadingZeros: false);

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
