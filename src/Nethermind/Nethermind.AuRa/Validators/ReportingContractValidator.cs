using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public class ReportingContractValidator : ContractValidator
    {
        public ReportingContractValidator(AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            ILogManager logManager,
            long startBlockNumber) : base(validator, stateProvider, abiEncoder, transactionProcessor, blockTree, logManager, startBlockNumber)
        {
        }

        public override AuRaParameters.ValidatorType Type { get; } = AuRaParameters.ValidatorType.ReportingContract;
    }
}