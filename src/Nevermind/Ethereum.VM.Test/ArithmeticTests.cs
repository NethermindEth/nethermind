using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class ArithmeticTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"ArithmeticTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}