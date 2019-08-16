using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;

namespace Nethermind.AuRa.Validators
{
    public interface IAuRaValidatorProcessor : IAuRaValidator, IAdditionalBlockProcessor
    {
        AuRaParameters.ValidatorType Type { get; }
    }
}