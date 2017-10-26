using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class PerformanceTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"Performance"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}