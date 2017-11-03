using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class BitwiseLogicTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"BitwiseLogicOperation"})]
        public void Test(VMTestBase.VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}