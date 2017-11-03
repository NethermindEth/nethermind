using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class RandomTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"RandomTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}