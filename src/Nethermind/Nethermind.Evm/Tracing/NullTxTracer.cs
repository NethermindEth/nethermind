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

namespace Nethermind.Evm.Tracing
{
    public class NullTxTracer : ITxTracer
    {
        private static NullTxTracer _instance;
        
        private NullTxTracer()
        {
        }

        public static NullTxTracer Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new NullTxTracer()); }
        }

        public bool IsTracing => false;
        public bool IsTracingCalls => false;
        public bool IsTracingStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingOpcodes => false;
        public bool IsTracingStack => false;
        public void MarkAsFailed()
        {
            throw new InvalidOperationException("Null tracer should never receive any calls.");
        }

        public void SetReturnValue(byte[] returnValue)
        {
            throw new InvalidOperationException("Null tracer should never receive any calls.");
        }
    }
}