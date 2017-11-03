using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class OtherTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Tests" })]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}