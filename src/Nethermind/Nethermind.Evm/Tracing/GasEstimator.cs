using System;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
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
            IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1);
            
            long intrinsicGas = tx.GasLimit - gasTracer.IntrinsicGasAt;
            if (tx.GasLimit > header.GasLimit)
            {
                return Math.Max(intrinsicGas, gasTracer.GasSpent + gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec));
            }

            tx.SenderAddress ??= Address.Zero; //If sender is not specified, use zero address.
            
            // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
            UInt256 leftBound = (gasTracer.GasSpent != 0 && gasTracer.GasSpent >= Transaction.BaseTxGasCost) 
                ? (UInt256) gasTracer.GasSpent - 1 
                : Transaction.BaseTxGasCost - 1;
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
            EstimateWithBinarySearchTracer tracer = new();
            transaction.GasLimit = (long)gasLimit;
            _transactionProcessor.CallAndRestore(transaction, block, tracer);
            
            return !(tracer.Error == "OutOfGas" || tracer.Error == "gas limit below intrinsic gas" || tracer.ReturnValue.SequenceEqual(Encoding.UTF8.GetBytes("OutOfGas")));
        }
    }
}
