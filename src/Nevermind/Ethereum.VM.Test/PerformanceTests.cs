using NCrunch.Framework;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class PerformanceTests : VMTestBase
    {
        [Isolated]
        [TestCaseSource(nameof(LoadTests), new object[] {"Performance"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}