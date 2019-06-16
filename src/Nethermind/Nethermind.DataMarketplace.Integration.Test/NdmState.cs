using Nethermind.Overseer.Test.Framework;

namespace Nethermind.DataMarketplace.Integration.Test
{
    public class NdmState : ITestState
    {
        public string DataHeaderId { get; set; }
        public string DepositId { get; set; }
    }
}