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
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class EvmMemoryTests
    {
        [Test]
        public void Trace_one_word()
        {
            EvmMemory memory = new EvmMemory();
            memory.SaveWord(0, new byte[EvmMemory.WordSize]);
            List<byte[]> trace =  memory.GetTrace();
            Assert.AreEqual(1, trace.Count);
        }
        
        [Test]
        public void Trace_two_words()
        {
            EvmMemory memory = new EvmMemory();
            memory.SaveWord(EvmMemory.WordSize, new byte[EvmMemory.WordSize]);
            List<byte[]> trace =  memory.GetTrace();
            Assert.AreEqual(2, trace.Count);
        }
        
        [Test]
        public void Trace_overwrite()
        {
            EvmMemory memory = new EvmMemory();
            memory.SaveWord(EvmMemory.WordSize, new byte[EvmMemory.WordSize]);
            memory.SaveWord(EvmMemory.WordSize, new byte[EvmMemory.WordSize]);
            List<byte[]> trace =  memory.GetTrace();
            Assert.AreEqual(2, trace.Count);
        }
    }
}