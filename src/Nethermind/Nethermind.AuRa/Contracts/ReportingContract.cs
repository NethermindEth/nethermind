using Nethermind.Abi;
using Nethermind.Core.Specs.ChainSpecStyle;

namespace Nethermind.AuRa.Contracts
{
    public class ReportingContract : ValidatorContract
    {
        public ReportingContract(IAbiEncoder abiEncoder, AuRaParameters auRaParameters) : base(abiEncoder)
        {
        }
    }
}