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
        Error = TryGetErrorMessage(span)
                ?? DefaultErrorMessage(span);
    }

    private string DefaultErrorMessage(ReadOnlySpan<byte> span)
    {
        return string.Concat(RevertedErrorMessagePrefix, span.ToHexString(true));
    }

    private unsafe string? TryGetErrorMessage(ReadOnlySpan<byte> span)
    {
        if (span.Length < RevertPrefix + sizeof(UInt256) * 2)
        {
            return null;
        }

        if (!span.Slice(0, RevertPrefix).IsZero())
        {
            // Fail if the prefix is not '0x00000000'
            return null;
        }

        try
        {
            int start = (int)new UInt256(span.Slice(RevertPrefix, sizeof(UInt256)), isBigEndian: true);
            if (checked(RevertPrefix + start + sizeof(UInt256)) > span.Length)
            {
                return null;
            }

            int length = (int)new UInt256(span.Slice(RevertPrefix + start, sizeof(UInt256)), isBigEndian: true);
            if (checked(RevertPrefix + start + sizeof(UInt256) + length) > span.Length)
            {
                return null;
            }

            return string.Concat(RevertedErrorMessagePrefix, span.Slice(RevertPrefix + start + sizeof(UInt256), length).ToHexString(true));
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
