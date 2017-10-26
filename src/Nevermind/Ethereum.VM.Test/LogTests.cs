using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class LogTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"LogTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}