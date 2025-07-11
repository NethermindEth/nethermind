// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Evm.State;
using Nethermind.State.Proofs;

namespace Evm.T8n.JsonTypes;

public class T8nExecutionResult
{
    public PostState PostState { get; set; } = new();
    public Dictionary<Address, AccountState> Accounts { get; set; } = [];
    public byte[] TransactionsRlp { get; set; } = [];

    public static T8nExecutionResult ConstructT8nExecutionResult(IWorldState stateProvider,
        Block block,
        T8nTest test,
        StorageTxTracer storageTracer,
        BlockReceiptsTracer blockReceiptsTracer,
        ISpecProvider specProvider,
        TransactionExecutionReport txReport)
    {
        IReceiptSpec receiptSpec = specProvider.GetSpec(block.Header);
        Hash256 txRoot = TxTrie.CalculateRoot(txReport.SuccessfulTransactions.ToArray());
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(receiptSpec,
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
        };

        T8nExecutionResult t8NExecutionResult = new()
        {
            PostState = postState,
            TransactionsRlp = Rlp.Encode(txReport.SuccessfulTransactions.ToArray()).Bytes,
            Accounts = CollectAccounts(test, stateProvider, storageTracer, block),
        };

        return t8NExecutionResult;
    }

    private static Dictionary<Address, AccountState> CollectAccounts(T8nTest test, IWorldState stateProvider, StorageTxTracer storageTracer, Block block)
    {
        Dictionary<Address, AccountState?> accounts = test.Alloc.Keys.ToDictionary(address => address,
            address => GetAccountState(address, stateProvider, storageTracer));

        accounts.AddRange(test.Ommers.ToDictionary(ommer => ommer.Address,
            ommer => GetAccountState(ommer.Address, stateProvider, storageTracer)));

        if (block.Beneficiary is not null)
        {
            accounts[block.Beneficiary] = GetAccountState(block.Beneficiary, stateProvider, storageTracer);
        }

        foreach (Transaction tx in test.Transactions)
        {
            if (tx.To is not null && !accounts.ContainsKey(tx.To))
            {
                accounts[tx.To] = GetAccountState(tx.To, stateProvider, storageTracer);
            }
            if (tx.SenderAddress is not null && !accounts.ContainsKey(tx.SenderAddress))
            {
                accounts[tx.SenderAddress] = GetAccountState(tx.SenderAddress, stateProvider, storageTracer);
            }
        }

        return accounts
            .Where(addressAndAccount => addressAndAccount.Value is not null)
            .ToDictionary(addressAndAccount => addressAndAccount.Key, addressAndAccount => addressAndAccount.Value!);
    }

    private static AccountState? GetAccountState(Address address, IWorldState stateProvider, StorageTxTracer storageTxTracer)
    {
        if (!stateProvider.AccountExists(address))  return null;

        stateProvider.TryGetAccount(address, out var account);
        var code = stateProvider.GetCode(address);
        var accountState = new AccountState
        {
            Nonce = account.Nonce,
            Balance = account.Balance,
            Code = code!
        };

        accountState.Storage = storageTxTracer.GetStorage(address) ?? [];

        return accountState;
    }
}
