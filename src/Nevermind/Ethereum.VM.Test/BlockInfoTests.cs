using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class BlockInfoTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"BlockInfoTest"})]
        public void Test(VirtualMachineTest test)
        {
            RunTest(test);
        }
    }
}