using NUnit.Framework;

namespace Ethereum.VM.Test
{
    internal class PushDupSwapTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"PushDupSwapTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}