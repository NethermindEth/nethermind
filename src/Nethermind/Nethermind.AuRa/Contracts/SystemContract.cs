using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.AuRa.Contracts
{
    public class SystemContract
    {
        private readonly Address _contractAddress;

        public SystemContract(Address contractAddress)
        {
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
        }
        
        protected Transaction GenerateTransaction(byte[] transactionData, long gasLimit = long.MaxValue, UInt256? nonce = null)
        {
            var transaction = new Transaction(true)
            {
                Value = UInt256.Zero,
                Data = transactionData,
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = gasLimit,
                GasPrice = UInt256.Zero,
                Nonce = nonce ?? UInt256.Zero,
            };
                
            transaction.Hash = Transaction.CalculateHash(transaction);

            return transaction;
        }
        
        public void InvokeTransaction(BlockHeader header, ITransactionProcessor transactionProcessor, Transaction transaction, CallOutputTracer tracer, bool isReadOnly = false)
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
                    throw new AuRaException($"System call returned error '{tracer.Error}' at block {header.Number}.");
                }
            }
        }
    }
}