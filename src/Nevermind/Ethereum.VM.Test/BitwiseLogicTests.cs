using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class BitwiseLogicTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"BitwiseLogicOperation"})]
        public void Test(TestsBase.VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}