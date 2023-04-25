// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;

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
                // TODO: is this invoked even if there is no tracer? why would we construct error messages then?
                Error = Revert;
                if (isTracerConnected)
                {
                    if (Output.Length > 0)
                    {
                        var span = Output.Span;
                        if (span.Length > 32 + 4)
                        {
                            try
                            {
                                BigInteger start = span.Slice(4, 32).ToUnsignedBigInteger();
                                BigInteger length = span.Slice((int)start + 4, 32).ToUnsignedBigInteger();
                                Error = string.Concat("Reverted ",
                                    Output.Slice((int)start + 32 + 4, (int)length).ToArray().ToHexString(true));
                            }
                            catch (Exception)
                            {
                                try
                                {
                                    Error = string.Concat("Reverted ", span.ToHexString(true));
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                        }
                        else
                        {
                            Error = string.Concat("Reverted ", span.ToHexString(true));
                        }
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
