using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class EnvironmentalInfoTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "EnvironmentalInfo" })]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}