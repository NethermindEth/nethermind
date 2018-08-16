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
            Assert.AreEqual(expectedResult, result);
        }
    }
}