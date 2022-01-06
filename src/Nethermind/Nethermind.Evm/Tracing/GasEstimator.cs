using System;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class GasEstimator
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly ISpecProvider _specProvider;

        public GasEstimator(ITransactionProcessor transactionProcessor, IStateProvider stateProvider, ISpecProvider specProvider)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
            _specProvider = specProvider;
        }

        public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer)
        {
            //TODO: check if we can use gasTracer estimate in more cases
            
            IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1);
            
            long intrinsicGas = tx.GasLimit - gasTracer.IntrinsicGasAt;
            if (tx.GasLimit > header.GasLimit)
            {
                return Math.Max(intrinsicGas, gasTracer.GasSpent + gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec));
            }

            tx.SenderAddress ??= Address.Zero; //If sender is not specified, use zero address.
            
            // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
            UInt256 leftBound = Transaction.BaseTxGasCost - 1;
            UInt256 rightBound = (tx.GasLimit != 0 && tx.GasPrice >= Transaction.BaseTxGasCost) 
                ? (UInt256)tx.GasLimit 
                : (UInt256)header.GasLimit;

            UInt256 senderBalance = _stateProvider.GetBalance(tx.SenderAddress);
            
            // Calculate and return additional gas required in case of insufficient funds.    
            if (tx.Value != UInt256.Zero && tx.Value >= senderBalance)
            {
                return gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
            }

            // Execute binary search to find the optimal gas estimation.
            return BinarySearchEstimate(leftBound, rightBound, rightBound, tx, header);
        }
        
        private long BinarySearchEstimate(UInt256 leftBound, UInt256 rightBound, UInt256 cap, Transaction tx, BlockHeader header)
        {
            while (leftBound + 1 < rightBound)
            {
                UInt256 mid = (leftBound + rightBound) / 2;
                if (!TryExecutableTransaction(tx, header, mid))
                {
                    leftBound = mid;
                }
                else
                {
                    rightBound = mid;
                }
            }

            if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound))
            {
                return 0;
            }

            return (long)(rightBound);   
        }        

        private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, UInt256 gasLimit)
        {
            // CallOutputTracer tracer = new CallOutputTracer();
            GethLikeTxTracer txTracer = new(GethTraceOptions.Default);
            transaction.GasLimit = (long)gasLimit;
            _transactionProcessor.CallAndRestore(transaction, block, txTracer);

            if (txTracer.BuildResult().Failed)
            {
                return false;
            }

            bool outOfGas = false;
            foreach (GethTxTraceEntry entry in txTracer.BuildResult().Entries)
            {
                string? error = entry.Error;
                if (error is "OutOfGas")
                {
                    outOfGas = true;
                    break;
                }
            }
            
            //TODO: Check if CallOutputTracer wouldn't be enough.
            // tracer.Error == "OutOfGas" || tracer.ReturnValue.SequenceEqual(Encoding.UTF8.GetBytes("OutOfGas"))

            return !outOfGas;
        }
    }

}

