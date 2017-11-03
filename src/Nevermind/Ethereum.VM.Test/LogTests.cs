using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class LogTests : VMTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"LogTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}