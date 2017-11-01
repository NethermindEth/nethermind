using NUnit.Framework;

namespace Ethereum.VM.Test
{
    [TestFixture]
    public class IoAndFlowTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"IOandFlowOperations"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}