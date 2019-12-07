using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public class ReportingContractValidator : ContractValidator
    {
        public ReportingContractValidator(AuRaParameters.Validator validator,
            IDb stateDb,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            long startBlockNumber) 
            : base(validator, stateDb, stateProvider, abiEncoder, transactionProcessor, blockTree, receiptStorage, logManager, startBlockNumber)
        {
        }
    }
}