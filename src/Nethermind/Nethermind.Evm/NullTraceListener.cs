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

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm
{
    public class NullTraceListener : ITraceListener
    {
        private static NullTraceListener _instance;

        private NullTraceListener()
        {
        }

        public static NullTraceListener Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullTraceListener()); }
        }

        public bool ShouldTrace(Keccak txHash)
        {
            return false;
        }

        public void RecordTrace(Keccak txHash, TransactionTrace trace)
        {
            throw new InvalidOperationException("I am not interested in this trace.");
        }
    }
}