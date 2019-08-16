using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.AuRa.Contracts
{
    public class SystemContract
    {
        protected static Transaction GenerateTransaction(Address contractAddress, byte[] transactionData, long blockNumber, long gasLimit, UInt256 nonce)
        {
            if (contractAddress == null) return null;
            
            var transaction = new Transaction
            {
                Value = 0,
                Data = transactionData,
                To = contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = 0.GWei(),
                Nonce = nonce,
            };
                
            transaction.Hash = Transaction.CalculateHash(transaction);

            return transaction;
        }
        
        public static void InvokeTransaction(BlockHeader header, ITransactionProcessor transactionProcessor, Transaction transaction, CallOutputTracer tracer, bool isReadOnly = false)
        {
            if (transaction != null)
            {
                if (isReadOnly)
                {
                    transactionProcessor.CallAndRestore(transaction, header, tracer);
                }
                else
                {
                    transactionProcessor.Execute(transaction, header, tracer);                    
                }
                
                if (tracer.StatusCode != StatusCode.Success)
                {
                    throw new AuRaException($"System call returned error {tracer.Error}.");
                }
            }
        }
    }
}