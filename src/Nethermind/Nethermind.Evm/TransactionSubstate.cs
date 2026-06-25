// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm;

public readonly ref struct TransactionSubstate
{
    private readonly ILogger _logger;

    private const string SomeError = "error";
    public const string Revert = "revert";

    private const int RevertPrefix = 4;
    private const int WordSize = EvmPooledMemory.WordSize;

    public static readonly byte[] ErrorFunctionSelector = Keccak.Compute("Error(string)").BytesToArray()[..RevertPrefix];
    public static readonly byte[] PanicFunctionSelector = Keccak.Compute("Panic(uint256)").BytesToArray()[..RevertPrefix];


    private static readonly FrozenDictionary<UInt256, string> PanicReasons = new Dictionary<UInt256, string>
    {
        { 0x00, "generic panic" },
        { 0x01, "assert(false)" },
        { 0x11, "arithmetic underflow or overflow" },
        { 0x12, "division or modulo by zero" },
        { 0x21, "enum overflow" },
        { 0x22, "invalid encoded storage byte array accessed" },
        { 0x31, "out-of-bounds array access; popping on an empty array" },
        { 0x32, "out-of-bounds access of an array or bytesN" },
        { 0x41, "out of memory" },
        { 0x51, "uninitialized function" },
    }.ToFrozenDictionary();

    private readonly JournalSet<Address>? _destroyList;
    private readonly JournalCollection<LogEntry> _logs;

    public bool IsError => Error is not null && !ShouldRevert;
    public string? Error { get; }
    public string? SubstateError { get; }
    public EvmExceptionType EvmExceptionType { get; }
    public ReadOnlyMemory<byte> Output { get; }
    public bool ShouldRevert { get; }
    public long Refund { get; }
    public JournalCollection<LogEntry> Logs => _logs;
    public JournalSet<Address>? DestroyList => _destroyList;

    public TransactionSubstate(EvmExceptionType exceptionType, bool isTracerConnected, string? substateError = null)
    {
        // Enum.ToString() faults in the trimmed zkVM runtime (no enum metadata); use the
        // reflection-free mapping. A top-level tx can legitimately fail and the receipts tracer
        // formats this error name.
        Error = isTracerConnected ? exceptionType.FastToString() : SomeError;
        SubstateError = substateError;
        EvmExceptionType = exceptionType;
        Refund = 0;
        _destroyList = null;
        // Real list, not a shared empty sentinel: EIP-7708 SELFDESTRUCT appends a transfer log and this readonly struct can't reassign later.
        _logs = [];
        ShouldRevert = false;
    }

    public TransactionSubstate(ReadOnlyMemory<byte> bytes,
        long refund,
        JournalSet<Address>? destroyList,
        JournalCollection<LogEntry>? logs,
        bool shouldRevert,
        bool isTracerConnected = default,
        EvmExceptionType evmExceptionType = default,
        ILogger logger = default)
    {
        _logger = logger;
        Output = bytes;
        Refund = refund;
        _destroyList = destroyList;
        _logs = logs ?? [];
        ShouldRevert = shouldRevert;
        EvmExceptionType = evmExceptionType;

        if (!ShouldRevert)
        {
            Error = null;
            return;
        }

        Error = Revert;

        if (!isTracerConnected)
            return;

        if (Output.IsEmpty)
            return;

        ReadOnlySpan<byte> span = Output.Span;
        if (TryGetErrorMessage(span) is { } decoded) Error = decoded;
    }

    public bool DestroyListContains(Address? address) => address is not null && _destroyList?.Contains(address) == true;

    public LogEntry[] LogsToArray() => _logs.ToArray();

    public static string EncodeErrorMessage(ReadOnlySpan<byte> span)
    {
        if (span.IndexOfAnyExceptInRange((byte)32, (byte)126) >= 0)
            return span.ToHexString(true);

        return Encoding.ASCII.GetString(span);
    }

    public static string? GetErrorMessage(ReadOnlySpan<byte> span)
    {
        if (span.Length < RevertPrefix) return null;
        ReadOnlySpan<byte> prefix = span.TakeAndMove(RevertPrefix);

        if (prefix.SequenceEqual(PanicFunctionSelector))
        {
            if (span.Length < WordSize) return null;

            UInt256 panicCode = new(span.TakeAndMove(WordSize), isBigEndian: true);
            if (!PanicReasons.TryGetValue(panicCode, out string panicReason))
            {
                return $"unknown panic code ({panicCode.ToHexString(skipLeadingZeros: true)})";
            }

            return panicReason;
        }

        if (prefix.SequenceEqual(ErrorFunctionSelector))
        {
            if (span.Length < WordSize * 2) return null;

            UInt256 start = new(span.TakeAndMove(WordSize), isBigEndian: true);
            if (start != WordSize) return null;

            UInt256 length = new(span.TakeAndMove(WordSize), isBigEndian: true);
            if (length > span.Length) return null;

            ReadOnlySpan<byte> binaryMessage = span.TakeAndMove((int)length);
            return EncodeErrorMessage(binaryMessage);
        }

        // Unknown selector — not Error(string) or Panic(uint256). Return null so the caller
        // falls back to the Revert sentinel, matching Geth's UnpackRevert default behaviour.
        return null;
    }

    /// <summary>
    /// Builds the user-facing revert message: <c>"execution reverted: &lt;reason&gt;"</c> when the
    /// revert payload carries a decodable <c>Error(string)</c>/<c>Panic(uint256)</c> selector and a
    /// reason was parsed, otherwise the bare <c>"execution reverted"</c> sentinel.
    /// </summary>
    /// <remarks>
    /// Shared by <c>eth_call</c>/<c>eth_estimateGas</c> and <c>proof_call</c> so they report identical
    /// text for the same revert. The selector is checked on the raw payload (not the decoded string) to
    /// avoid a sentinel collision — e.g. <c>require(false, "execution reverted")</c> must not be taken
    /// for the bare sentinel.
    /// </remarks>
    public static string BuildRevertMessage(ReadOnlySpan<byte> revertPayload, string? reason)
    {
        bool isKnownRevertType = revertPayload.Length >= RevertPrefix &&
            (revertPayload[..RevertPrefix].SequenceEqual(ErrorFunctionSelector) ||
             revertPayload[..RevertPrefix].SequenceEqual(PanicFunctionSelector));

        return isKnownRevertType && !string.IsNullOrEmpty(reason)
            ? "execution reverted: " + reason
            : "execution reverted";
    }

    private string? TryGetErrorMessage(ReadOnlySpan<byte> span)
    {
        try
        {
            return GetErrorMessage(span);
        }
        catch (Exception e) // shouldn't happen, just for being safe
        {
            if (_logger.IsError) _logger.Error("Couldn't parse revert message", e);
            return null;
        }
    }
}
