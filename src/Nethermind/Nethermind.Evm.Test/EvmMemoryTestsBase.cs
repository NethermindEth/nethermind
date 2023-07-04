// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
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
            UInt256 dest = (UInt256)int.MaxValue + 1;
            memory.Save(in dest, new byte[0]);
        }

        [Test]
        public void Trace_one_word()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = UInt256.Zero;
            memory.SaveWord(in dest, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.That(trace.Count, Is.EqualTo(1));
        }

        [Test]
        public void Trace_two_words()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = EvmPooledMemory.WordSize;
            memory.SaveWord(in dest, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.That(trace.Count, Is.EqualTo(2));
        }

        [Test]
        public void Trace_overwrite()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = EvmPooledMemory.WordSize;
            memory.SaveWord(in dest, new byte[EvmPooledMemory.WordSize]);
            memory.SaveWord(in dest, new byte[EvmPooledMemory.WordSize]);
            List<string> trace = memory.GetTrace();
            Assert.That(trace.Count, Is.EqualTo(2));
        }

        [Test]
        public void Trace_when_position_not_on_word_border()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = EvmPooledMemory.WordSize / 2;
            memory.SaveByte(in dest, 1);
            List<string> trace = memory.GetTrace();
            Assert.That(trace.Count, Is.EqualTo(1));
        }

        [Test]
        public void Calculate_memory_cost_returns_0_for_subsequent_calls()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = UInt256.One;
            long cost1 = memory.CalculateMemoryCost(in dest, UInt256.One);
            long cost2 = memory.CalculateMemoryCost(in dest, UInt256.One);
            Assert.That(cost2, Is.EqualTo(0L));
        }

        [Test]
        public void Calculate_memory_cost_returns_0_for_0_length()
        {
            IEvmMemory memory = CreateEvmMemory();
            UInt256 dest = long.MaxValue;
            long cost = memory.CalculateMemoryCost(in dest, UInt256.Zero);
            Assert.That(cost, Is.EqualTo(0L));
        }
    }
}
