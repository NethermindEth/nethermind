using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class Sha3Tests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"Sha3Test"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}