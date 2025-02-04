// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Evm.T8n.JsonTypes;

public class T8nExecutionResult
{
    public PostState PostState { get; set; } = new();
    public Dictionary<Address, AccountState> Accounts { get; set; } = [];
    public byte[] TransactionsRlp { get; set; } = [];

    public static T8nExecutionResult ConstructT8nExecutionResult(WorldState stateProvider,
        Block block,
        T8nTest test,
        StorageTxTracer storageTracer,
        List<ProofTxTracer> proofTxTracerList,
        BlockReceiptsTracer blockReceiptsTracer,
        ISpecProvider specProvider,
        TransactionExecutionReport txReport)
    {
        IReceiptSpec receiptSpec = specProvider.GetSpec(block.Header);
        Hash256 txRoot = TxTrie.CalculateRoot(txReport.SuccessfulTransactions.ToArray());
        Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec,
            txReport.SuccessfulTransactionReceipts.ToArray(), new ReceiptMessageDecoder());
        LogEntry[] logEntries = txReport.SuccessfulTransactionReceipts
            .SelectMany(receipt => receipt.Logs ?? Enumerable.Empty<LogEntry>())
            .ToArray();
        var bloom = new Bloom(logEntries);
        var gasUsed = blockReceiptsTracer.TxReceipts.Count == 0 ? 0 : (ulong)blockReceiptsTracer.LastReceipt.GasUsedTotal;
        ulong? blobGasUsed = test.Spec.IsEip4844Enabled ? BlobGasCalculator.CalculateBlobGas(txReport.ValidTransactions.ToArray()) : null;

        var postState = new PostState
        {
            StateRoot = stateProvider.StateRoot,
            TxRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            WithdrawalsRoot = block.WithdrawalsRoot,
            LogsHash = Keccak.Compute(Rlp.OfEmptySequence.Bytes),
            LogsBloom = bloom,
            Receipts = txReport.SuccessfulTransactionReceipts.ToArray(),
            Rejected = txReport.RejectedTransactionReceipts.Count == 0
                ? null
                : txReport.RejectedTransactionReceipts.ToArray(),
            CurrentDifficulty = test.CurrentDifficulty,
            GasUsed = new UInt256(gasUsed),
            CurrentBaseFee = test.CurrentBaseFee,
            CurrentExcessBlobGas = block.ExcessBlobGas,
            BlobGasUsed = blobGasUsed,
            RequestsHash = block.RequestsHash,
            Requests = block.ExecutionRequests,
        };

        T8nExecutionResult t8NExecutionResult = new()
        {
            PostState = postState,
            TransactionsRlp = Rlp.Encode(txReport.SuccessfulTransactions.ToArray()).Bytes,
            Accounts = CollectAccounts(test, stateProvider, storageTracer, block, proofTxTracerList),
        };

        return t8NExecutionResult;
    }

    private static Dictionary<Address, AccountState> CollectAccounts(T8nTest test, WorldState stateProvider,
        StorageTxTracer storageTracer, Block block, List<ProofTxTracer> proofTxTracerList)
    {
        var addresses = CollectAccountAddresses(test, block, proofTxTracerList);
        Dictionary<Address, AccountState> accounts = new();
        foreach (var address in addresses)
        {
            List<UInt256> storageKeys = storageTracer.GetStorageKeys(address);
            if (test.Alloc.TryGetValue(address, out var accountsState))
            {
                storageKeys.AddRange(accountsState.Storage.Keys);
            }

            accountsState = GetAccountState(address, stateProvider, storageKeys);
            if (accountsState is not null) accounts.Add(address, accountsState);
        }

        return accounts;
    }

    private static HashSet<Address> CollectAccountAddresses(T8nTest test, Block block, List<ProofTxTracer> proofTxTracerList)
    {
        HashSet<Address> addresses = [];
        addresses.AddRange(test.Alloc.Keys);
        addresses.AddRange(test.Ommers.Select(ommer => ommer.Address));
        addresses.AddRange(block.Withdrawals?.Select(withdrawal => withdrawal.Address) ?? []);
        if (block.Beneficiary is not null) addresses.Add(block.Beneficiary);
        foreach (Transaction tx in test.Transactions)
        {
            if (tx.SenderAddress is not null) addresses.Add(tx.SenderAddress);
            if (tx.To is not null) addresses.Add(tx.To);
        }
        addresses.AddRange(proofTxTracerList.SelectMany(tracer => tracer.Accounts));

        return addresses;
    }

    private static AccountState? GetAccountState(Address address, WorldState stateProvider, List<UInt256> storageKeys)
    {
        if (!stateProvider.AccountExists(address))  return null;

        Account account = stateProvider.GetAccount(address);
        var code = stateProvider.GetCode(address);
        var accountState = new AccountState
        {
            Nonce = account.Nonce,
            Balance = account.Balance,
            Code = code
        };

        foreach (UInt256 storageKey in storageKeys)
        {
            ReadOnlySpan<byte> value = stateProvider.Get(new StorageCell(address, storageKey));
            if (!value.IsEmpty) accountState.Storage[storageKey] = value.ToArray();
        }

        return accountState;
    }
}
