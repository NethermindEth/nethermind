// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public class TransactionSubstate
    {
        private static List<Address> _emptyDestroyList = new(0);
        private static List<LogEntry> _emptyLogs = new(0);

        private const string SomeError = "error";
        private const string Revert = "revert";

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
            if (ShouldRevert)
            {
                Error = Revert;
                if (isTracerConnected)
                {
                    if (Output.Length > 0)
                    {
                        ReadOnlySpan<byte> span = Output.Span;
                        if (span.Length >= 32 * 2 + 4)
                        {
                            try
                            {
                                int start = (int)(new UInt256(span.Slice(4, 32), isBigEndian: true));
                                if (start + 4 + 32 <= span.Length)
                                {
                                    int length = (int)new UInt256(span.Slice(start + 4, 32), isBigEndian: true);
                                    if (checked(start + 4 + 32 + length) <= span.Length)
                                    {
                                        Error = string.Concat("Reverted ",
                                            span.Slice(start + 32 + 4, length).ToHexString(true));
                                        return;
                                    }
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        Error = string.Concat("Reverted ", span.ToHexString(true));
                    }
                }
            }
            else
            {
                Error = null;
            }
        }

        public bool IsError => Error is not null && !ShouldRevert;

        public string Error { get; }

        public ReadOnlyMemory<byte> Output { get; }

        public bool ShouldRevert { get; }

        public long Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}
