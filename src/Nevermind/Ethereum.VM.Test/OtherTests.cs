using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class OtherTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Tests" })]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}