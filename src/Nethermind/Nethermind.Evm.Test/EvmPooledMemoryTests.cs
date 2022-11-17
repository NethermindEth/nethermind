// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class EvmPooledMemoryTests : EvmMemoryTestsBase
    {
        protected override IEvmMemory CreateEvmMemory()
        {
            return new EvmPooledMemory();
        }

        [TestCase(32, 1)]
        [TestCase(0, 0)]
        [TestCase(33, 2)]
        [TestCase(64, 2)]
        [TestCase(int.MaxValue, int.MaxValue / 32 + 1)]
        public void Div32Ceiling(int input, int expectedResult)
        {
            long result = EvmPooledMemory.Div32Ceiling((ulong)input);
            TestContext.WriteLine($"Memory cost (gas): {result}");
            Assert.AreEqual(expectedResult, result);
        }

        private const int MaxCodeSize = 24576;

        [TestCase(0, 0)]
        [TestCase(0, 32)]
        [TestCase(0, 256)]
        [TestCase(0, 2048)]
        [TestCase(0, MaxCodeSize)]
        [TestCase(10 * MaxCodeSize, MaxCodeSize)]
        [TestCase(100 * MaxCodeSize, MaxCodeSize)]
        [TestCase(1000 * MaxCodeSize, MaxCodeSize)]
        [TestCase(0, 1024 * 1024)]
        [TestCase(0, Int32.MaxValue)]
        public void MemoryCost(int destination, int memoryAllocation)
        {
            EvmPooledMemory memory = new();
            UInt256 dest = (UInt256)destination;
            long result = memory.CalculateMemoryCost(in dest, (UInt256)memoryAllocation);
            TestContext.WriteLine($"Gas cost of allocating {memoryAllocation} starting from {dest}: {result}");
        }

        [Test]
        public void Inspect_should_not_change_evm_memory()
        {
            EvmPooledMemory memory = new();
            memory.Save(3, TestItem.KeccakA.Bytes);
            ulong initialSize = memory.Size;
            ReadOnlyMemory<byte> result = memory.Inspect(initialSize + 32, 32);
            Assert.AreEqual(initialSize, memory.Size);
            Assert.AreEqual(ReadOnlyMemory<byte>.Empty, result);
        }

        [Test]
        public void Inspect_can_read_memory()
        {
            const int offset = 3;
            byte[] expectedEmptyRead = new byte[32 - offset];
            byte[] expectedKeccakRead = TestItem.KeccakA.Bytes;
            EvmPooledMemory memory = new();
            memory.Save((UInt256)offset, expectedKeccakRead);
            ulong initialSize = memory.Size;
            ReadOnlyMemory<byte> actualKeccakMemoryRead = memory.Inspect((UInt256)offset, 32);
            ReadOnlyMemory<byte> actualEmptyRead = memory.Inspect(32 + (UInt256)offset, 32 - (UInt256)offset);
            Assert.AreEqual(initialSize, memory.Size);
            Assert.AreEqual(expectedKeccakRead, actualKeccakMemoryRead.ToArray());
            Assert.AreEqual(expectedEmptyRead, actualEmptyRead.ToArray());
        }

        [Test]
        public void Load_should_update_size_of_memory()
        {
            byte[] expectedResult = new byte[32];
            EvmPooledMemory memory = new();
            memory.Save(3, TestItem.KeccakA.Bytes);
            ulong initialSize = memory.Size;
            ReadOnlyMemory<byte> result = memory.Load(initialSize + 32, 32);
            Assert.AreNotEqual(initialSize, memory.Size);
            Assert.AreEqual(expectedResult, result.ToArray());
        }
    }
}
