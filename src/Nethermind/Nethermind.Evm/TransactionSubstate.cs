// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm;

public class TransactionSubstate
{
    private static readonly List<Address> _emptyDestroyList = new(0);
    private static readonly List<LogEntry> _emptyLogs = new(0);

    private const string SomeError = "error";
    private const string Revert = "revert";

    private const int RevertPrefix = 4;
    private const int WordSize = EvmPooledMemory.WordSize;

    private const string RevertedErrorMessagePrefix = "Reverted ";
    public static readonly byte[] ErrorFunctionSelector = Keccak.Compute("Error(string)").BytesToArray()[..RevertPrefix];
    public static readonly byte[] PanicFunctionSelector = Keccak.Compute("Panic(uint256)").BytesToArray()[..RevertPrefix];

    private readonly IDictionary<UInt256, string> PanicReasons = new Dictionary<UInt256, string>
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
    };

    public bool IsError => Error is not null && !ShouldRevert;
    public string? Error { get; }
    public ReadOnlyMemory<byte> Output { get; }
    public bool ShouldRevert { get; }
    public long Refund { get; }
    public IReadOnlyCollection<LogEntry> Logs { get; }
    public IReadOnlyCollection<Address> DestroyList { get; }

    public TransactionSubstate(EvmExceptionType exceptionType, bool isTracerConnected)
    {
        Error = isTracerConnected ? exceptionType.ToString() : SomeError;
        Refund = 0;
        DestroyList = _emptyDestroyList;
        Logs = _emptyLogs;
        ShouldRevert = false;
    }

    public TransactionSubstate(
        ReadOnlyMemory<byte> output,
        long refund,
        IReadOnlyCollection<Address> destroyList,
        IReadOnlyCollection<LogEntry> logs,
        bool shouldRevert,
        bool isTracerConnected)
    {
        Output = output;
        Refund = refund;
        DestroyList = destroyList;
        Logs = logs;
        ShouldRevert = shouldRevert;

        if (!ShouldRevert)
        {
            Error = null;
            return;
        }

        Error = Revert;

        if (!isTracerConnected)
            return;

        if (Output.Length <= 0)
            return;

        ReadOnlySpan<byte> span = Output.Span;
        Error = string.Concat(
            RevertedErrorMessagePrefix,
            TryGetErrorMessage(span) ?? DefaultErrorMessage(span)
        );
    }

    private static string DefaultErrorMessage(ReadOnlySpan<byte> span) => span.ToHexString(true);

    private string? TryGetErrorMessage(ReadOnlySpan<byte> span)
    {
        if (span.Length < RevertPrefix) goto UTF;
        ReadOnlySpan<byte> prefix = span.TakeAndMove(RevertPrefix);
        UInt256 start, length;

        if (prefix.SequenceEqual(PanicFunctionSelector))
        {
            if (span.Length < WordSize) goto UTF;

            UInt256 panicCode = new(span.TakeAndMove(WordSize), isBigEndian: true);
            if (!PanicReasons.TryGetValue(panicCode, out string panicReason))
            {
                return $"unknown panic code ({panicCode.ToHexString(skipLeadingZeros: true)})";
            }

            return panicReason;
        }

        if (prefix.SequenceEqual(ErrorFunctionSelector))
        {
            if (span.Length < WordSize * 2) goto UTF;

            start = new UInt256(span.TakeAndMove(WordSize), isBigEndian: true);
            if (start != WordSize) goto UTF;

            length = new UInt256(span.TakeAndMove(WordSize), isBigEndian: true);
            if (length > span.Length) goto UTF;

            ReadOnlySpan<byte> binaryMessage = span.TakeAndMove((int)length);
            string message = System.Text.Encoding.UTF8.GetString(binaryMessage);

            return message;
        }

        if (span.Length < WordSize * 2) goto UTF;

        start = new UInt256(span.Slice(0, WordSize), isBigEndian: true);
        if (UInt256.AddOverflow(start, WordSize, out UInt256 lengthOffset) || lengthOffset > span.Length) goto UTF;

        length = new UInt256(span.Slice((int)start, WordSize), isBigEndian: true);
        if (UInt256.AddOverflow(lengthOffset, length, out UInt256 endOffset) || endOffset != span.Length) goto UTF;

        return span.Slice((int)lengthOffset, (int)length).ToHexString(true);

        UTF:
        return System.Text.Encoding.UTF8.GetString(span);
    }
}
