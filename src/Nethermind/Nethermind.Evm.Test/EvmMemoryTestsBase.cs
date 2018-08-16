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
using System.Numerics;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public abstract class EvmMemoryTestsBase
    {
        protected abstract IEvmMemory CreateEvmMemory();

        [Test]
        public void Save_empty_beyond_reasonable_size_does_not_throw()
        {
            IEvmMemory memory = CreateEvmMemory();
            memory.Save((UInt256)int.MaxValue + 1, new byte[0]);
        }

        [Test]
        public void Trace_one_word()
        {
            IEvmMemory memory = CreateEvmMemory();
            memory.SaveWord(0, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.AreEqual(1, trace.Count);
        }

        [Test]
        public void Trace_two_words()
        {
            IEvmMemory memory = CreateEvmMemory();
            memory.SaveWord(EvmPooledMemory.WordSize, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.AreEqual(2, trace.Count);
        }

        [Test]
        public void Trace_overwrite()
        {
            IEvmMemory memory = CreateEvmMemory();
            memory.SaveWord(EvmPooledMemory.WordSize, new byte[EvmPooledMemory.WordSize]);
            memory.SaveWord(EvmPooledMemory.WordSize, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.AreEqual(2, trace.Count);
        }

        [Test]
        public void Trace_when_position_not_on_word_border()
        {
            IEvmMemory memory = CreateEvmMemory();
            memory.SaveByte(EvmPooledMemory.WordSize / 2, new byte[] {1});
            List<string> trace = memory.GetTrace();
            Assert.AreEqual(1, trace.Count);
        }

        [Test]
        public void Calculate_memory_cost_returns_0_for_subsequent_calls()
        {
            IEvmMemory memory = CreateEvmMemory();
            long cost1 = memory.CalculateMemoryCost(UInt256.One, UInt256.One);
            long cost2 = memory.CalculateMemoryCost(UInt256.One, UInt256.One);
            Assert.AreEqual(0L, cost2);
        }
        
        [Test]
        public void Calculate_memory_cost_returns_0_for_0_length()
        {
            IEvmMemory memory = CreateEvmMemory();
            long cost = memory.CalculateMemoryCost(long.MaxValue, UInt256.Zero);
            Assert.AreEqual(0L, cost);
        }
    }
}