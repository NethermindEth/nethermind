using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.Tracing
{
    public class EstimateGasService
    {

        public bool TryExecutableTransaction(Transaction transaction, BlockHeader block, UInt256 gas,
            ITransactionProcessor transactionProcessor)
        {
            GethLikeTxTracer txTracer = new(GethTraceOptions.Default);
            transaction.GasLimit = (long)gas;
            transactionProcessor.CallAndRestore(transaction, block, txTracer);

            if (txTracer.BuildResult().Failed) return false;

            bool outOfGas = false;
            foreach (var entry in txTracer.BuildResult().Entries)
            {
                string? error = entry.Error;
                if (error != null && error.Equals("OutOfGas"))
                {
                    outOfGas = true;
                    break;
                }
            }

            return !outOfGas;
        }
    }

}

