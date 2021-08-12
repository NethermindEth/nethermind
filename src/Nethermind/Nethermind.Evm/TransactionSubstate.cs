//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
                        try
                        {
                            BigInteger start = Output.Span.Slice(4, 32).ToUnsignedBigInteger();
                            BigInteger length = Output.Slice((int) start + 4, 32).Span.ToUnsignedBigInteger();
                            Error = string.Concat("Reverted ",
                                Output.Slice((int)start + 32 + 4, (int)length).ToArray().ToHexString(true));
                        }
                        catch (Exception)
                        {
                            try
                            {
                                Error = string.Concat("Reverted ", Output.ToArray().ToHexString(true));
                            }
                            catch
                            {
                                // ignore
                            }
                        }
                    }
                }
            }
            else
            {
                Error = null;
            }
        }

        public bool IsError => Error != null && !ShouldRevert;

        public string Error { get; }

        public ReadOnlyMemory<byte> Output { get; }

        public bool ShouldRevert { get; }

        public long Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}
