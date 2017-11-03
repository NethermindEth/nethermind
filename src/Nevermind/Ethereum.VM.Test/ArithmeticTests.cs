using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class ArithmeticTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"ArithmeticTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}