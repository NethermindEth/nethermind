using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class IoAndFlowTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"IOandFlowOperations"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}