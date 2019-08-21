using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;

namespace Nethermind.AuRa.Contracts
{
    public class ReportingContract : ValidatorContract
    {
        public ReportingContract(IAbiEncoder abiEncoder, Address contractAddress) : base(abiEncoder, contractAddress)
        {
        }
    }
}