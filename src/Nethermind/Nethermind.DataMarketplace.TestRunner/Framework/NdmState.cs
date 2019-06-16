using Nethermind.DataMarketplace.TestRunner.Tester;

namespace Nethermind.DataMarketplace.TestRunner.Framework
{
    public class NdmState : ITestState
    {
        public string DataHeaderId { get; set; }
        public string DepositId { get; set; }
    }
}