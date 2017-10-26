using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class OandFlowTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"OanFlowTests"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}