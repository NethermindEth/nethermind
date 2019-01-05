/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm
{
    public class TransactionSubstate
    {
        private static List<Address> _emptyDestroyList = new List<Address>(0);
        private static List<LogEntry> _emptyLogs = new List<LogEntry>(0);
        
        public TransactionSubstate(string error)
        {
            Error = error;
            Refund = 0;
            DestroyList = _emptyDestroyList;
            Logs = _emptyLogs;
            ShouldRevert = false;
        }
        
        public TransactionSubstate(
            byte[] output,
            long refund,
            IReadOnlyCollection<Address> destroyList,
            IReadOnlyCollection<LogEntry> logs,
            bool shouldRevert)
        {
            Output = output;
            Refund = refund;
            DestroyList = destroyList;
            Logs = logs;
            ShouldRevert = shouldRevert;
            Error = ShouldRevert ? "revert" : null;
        }

        public bool IsError => Error != null && !ShouldRevert;
        
        public string Error { get; }
        
        public byte[] Output { get; }
        
        public bool ShouldRevert { get; }

        public long Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}