using Nethermind.AuRa.Validators;
using Nethermind.Core.Specs.ChainSpecStyle;

namespace Nethermind.AuRa
{
    public interface IAuRaAdditionalBlockProcessorFactory
    {
        IAuRaValidatorProcessor CreateValidatorProcessor(AuRaParameters.Validator validator, long? startBlock = null);
    }
}