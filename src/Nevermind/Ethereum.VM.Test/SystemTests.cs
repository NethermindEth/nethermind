using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class SystemTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"SystemOperations"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}