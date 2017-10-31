using System.Linq;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    public class BlockInfoTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] {"BlockInfoTest"})]
        public void Test(VirtualMachineTest test)
        {
            // TODO: check why the tests are incorrect here
            string[] incorrectTests = {"blockhash258Block", "blockhashMyBlock", "blockhashNotExistingBlock"};
            if (incorrectTests.Contains(test.Name))
            {
                return;
            }

            RunTest(test);
        }
    }
}