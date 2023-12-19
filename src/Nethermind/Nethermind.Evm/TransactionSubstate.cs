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
    private readonly byte[] ErrorFunctionSelector = Keccak.Compute("Error(string)").BytesToArray()[..RevertPrefix];
    private readonly byte[] PanicFunctionSelector = Keccak.Compute("Panic(uint256)").BytesToArray()[..RevertPrefix];

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
        if (span.Length < RevertPrefix) { return null; }
        ReadOnlySpan<byte> prefix = span.TakeAndMove(RevertPrefix);

        if (prefix.SequenceEqual(PanicFunctionSelector))
        {
            if (span.Length < WordSize) { return null; }

            UInt256 panicCode = new(span.TakeAndMove(WordSize), isBigEndian: true);
            if (!PanicReasons.TryGetValue(panicCode, out string panicReason))
            {
                return $"unknown panic code ({panicCode.ToHexString(skipLeadingZeros: true)})";
            }

            return panicReason;
        }

        if (prefix.SequenceEqual(ErrorFunctionSelector))
        {
            if (span.Length < WordSize * 2) { return null; }

            int start = (int)new UInt256(span.TakeAndMove(WordSize), isBigEndian: true);
            if (start != WordSize) { return null; }

            int length = (int)new UInt256(span.TakeAndMove(WordSize), isBigEndian: true);
            if (length > span.Length) { return null; }

            ReadOnlySpan<byte> binaryMessage = span.TakeAndMove(length);
            string message = System.Text.Encoding.UTF8.GetString(binaryMessage);

            return message;
        }

        try
        {
            if (span.Length < WordSize * 2) { return null; }

            int start = (int)new UInt256(span.Slice(0, WordSize), isBigEndian: true);
            if (checked(start + WordSize) > span.Length) { return null; }

            int length = (int)new UInt256(span.Slice(start, WordSize), isBigEndian: true);
            if (checked(start + WordSize + length) != span.Length) { return null; }

            return span.Slice(start + WordSize, length).ToHexString(true);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
