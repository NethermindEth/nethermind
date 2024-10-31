// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Ethereum.Test.Base;
using Evm.t8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Evm.t8n;

public class T8NResult
{
    public Hash256? StateRoot { get; set; }
    public Hash256? TxRoot { get; set; }
    public Hash256? ReceiptsRoot { get; set; }
    public Hash256? WithdrawalsRoot { get; set; }
    public Hash256? LogsHash { get; set; }
    public Bloom? LogsBloom { get; set; }
    public TxReceipt[]? Receipts { get; set; }
    public RejectedTx[]? Rejected { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? CurrentDifficulty { get; set; }
    public UInt256? GasUsed { get; set; }
    public UInt256? CurrentBaseFee { get; set; }
    public UInt256? CurrentExcessBlobGas { get; set; }
    public UInt256? BlobGasUsed { get; set; }
    public Dictionary<Address, AccountState> Accounts { get; set; } = [];
    public byte[] TransactionsRlp { get; set; } = [];


    public static T8NResult ConstructT8NResult(WorldState stateProvider,
        Block block,
        T8NTest test,
        StorageTxTracer storageTracer,
        BlockReceiptsTracer blockReceiptsTracer,
        ISpecProvider specProvider,
        TransactionExecutionReport txReport)
    {
        T8NResult t8NResult = new();

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

        t8NResult.StateRoot = stateProvider.StateRoot;
        t8NResult.TxRoot = txRoot;
        t8NResult.ReceiptsRoot = receiptsRoot;
        t8NResult.LogsBloom = bloom;
        t8NResult.LogsHash = Keccak.Compute(Rlp.OfEmptySequence.Bytes);
        t8NResult.Receipts = txReport.SuccessfulTransactionReceipts.ToArray();
        t8NResult.Rejected = txReport.RejectedTransactionReceipts.Count == 0 ? null : txReport.RejectedTransactionReceipts.ToArray();
        t8NResult.CurrentDifficulty = test.CurrentDifficulty;
        t8NResult.GasUsed = new UInt256(gasUsed);
        t8NResult.CurrentBaseFee = test.CurrentBaseFee;
        t8NResult.WithdrawalsRoot = block.WithdrawalsRoot;
        t8NResult.CurrentExcessBlobGas = block.ExcessBlobGas;
        t8NResult.BlobGasUsed = blobGasUsed;
        t8NResult.TransactionsRlp = Rlp.Encode(txReport.SuccessfulTransactions.ToArray()).Bytes;
        t8NResult.Accounts = CollectAccounts(test, stateProvider, storageTracer, block);

        return t8NResult;
    }

    private static Dictionary<Address, AccountState> CollectAccounts(T8NTest test, WorldState stateProvider, StorageTxTracer storageTracer, Block block)
    {
        Dictionary<Address, AccountState?> accounts = test.Alloc.Keys.ToDictionary(address => address,
            address => GetAccountState(address, stateProvider, storageTracer.Storages));

        accounts.AddRange(test.Ommers.ToDictionary(ommer => ommer.Address,
            ommer => GetAccountState(ommer.Address, stateProvider, storageTracer.Storages)));

        if (block.Beneficiary != null)
        {
            accounts[block.Beneficiary] = GetAccountState(block.Beneficiary, stateProvider, storageTracer.Storages);
        }

        foreach (Transaction tx in test.Transactions)
        {
            if (tx.To is not null && !accounts.ContainsKey(tx.To))
            {
                accounts[tx.To] = GetAccountState(tx.To, stateProvider, storageTracer.Storages);
            }
            if (tx.SenderAddress is not null && !accounts.ContainsKey(tx.SenderAddress))
            {
                accounts[tx.SenderAddress] = GetAccountState(tx.SenderAddress, stateProvider, storageTracer.Storages);
            }
        }

        return accounts
            .Where(addressAndAccount => addressAndAccount.Value is not null)
            .ToDictionary(addressAndAccount => addressAndAccount.Key, addressAndAccount => addressAndAccount.Value!);
    }

    private static AccountState? GetAccountState(Address address, WorldState stateProvider, Dictionary<Address, Dictionary<UInt256, byte[]>> storages)
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

        if (storages.TryGetValue(address, out Dictionary<UInt256, byte[]>? storage))
        {
            accountState.Storage = storage;
        }

        return accountState;
    }
}
