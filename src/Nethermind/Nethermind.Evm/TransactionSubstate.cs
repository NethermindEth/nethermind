// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
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

    private const string RevertedErrorMessagePrefix = "Reverted ";
    private readonly byte[] ErrorFunctionSelector = { 0x08, 0xc3, 0x79, 0xa0 };

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
        Error = TryGetErrorMessage(span) ?? DefaultErrorMessage(span);
    }

    private string DefaultErrorMessage(ReadOnlySpan<byte> span) => string.Concat(RevertedErrorMessagePrefix, span.ToHexString(true));

    private string? TryGetErrorMessage(ReadOnlySpan<byte> span)
    {
        const int fieldLength = EvmPooledMemory.WordSize;
        if (span.Length < RevertPrefix + fieldLength * 2)
        {
            return null;
        }

        try
        {
            int start = (int)new UInt256(span.Slice(RevertPrefix, fieldLength), isBigEndian: true);

            int lengthStart = RevertPrefix + start;
            int lengthEnd = checked(lengthStart + fieldLength);
            if (lengthEnd <= span.Length)
            {
                int messageLength = (int)new UInt256(span.Slice(lengthStart, fieldLength), isBigEndian: true);
                int messageEnd = checked(lengthEnd + messageLength);
                if (messageEnd <= span.Length)
                {
                    ReadOnlySpan<byte> message = span.Slice(lengthEnd, messageLength);

                    if (messageEnd == span.Length)
                    {
                        return string.Concat(RevertedErrorMessagePrefix, message.ToHexString(true));
                    }

                    if (span[..RevertPrefix].SequenceEqual(ErrorFunctionSelector) && start == fieldLength)
                    {
                        return string.Concat(RevertedErrorMessagePrefix, System.Text.Encoding.UTF8.GetString(message));
                    }
                }
            }
        }
        catch (OverflowException) { }

        return null;
    }
}
