using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class SystemTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"SystemOperations"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}