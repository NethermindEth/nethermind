// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm.State;
using Nethermind.State.Proofs;

namespace Evm.T8n.JsonTypes;

public class T8nExecutionResult
{
    /// <summary>The block hash geth's <c>t8n</c> puts in receipts and logs, as the real one is not known yet.</summary>
    private static readonly Hash256 PlaceholderBlockHash = new("0x1337000000000000000000000000000000000000000000000000000000000000");

    public PostState PostState { get; set; } = new();
    public Dictionary<Address, AccountState> Accounts { get; set; } = [];
    public byte[] TransactionsRlp { get; set; } = [];

    public static T8nExecutionResult ConstructT8nExecutionResult(IWorldState stateProvider,
        StateWriteRecorder stateWrites,
        Block block,
        T8nTest test,
        TransactionExecutionReport txReport)
    {
        TxReceipt[] receipts = [.. txReport.SuccessfulTransactionReceipts];
        Hash256 txRoot = TxTrie.CalculateRoot(txReport.SuccessfulTransactions.ToArray());
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(test.Spec, receipts, new ReceiptMessageDecoder());
        LogEntry[] logEntries = receipts
            .SelectMany(receipt => receipt.Logs ?? [])
            .ToArray();
        ulong gasUsed = receipts.Length == 0 ? 0UL : receipts[^1].GasUsedTotal;
        bool hasBlobGas = block.ExcessBlobGas is not null;

        PostState postState = new()
        {
            StateRoot = stateProvider.StateRoot,
            TxRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            WithdrawalsRoot = block.WithdrawalsRoot,
            LogsHash = Keccak.Compute(Rlp.Encode(logEntries).Bytes),
            LogsBloom = new Bloom(logEntries),
            Receipts = BuildReceipts(block, test.Spec, txReport.SuccessfulTransactions, receipts),
            Rejected = txReport.RejectedTransactionReceipts.Count == 0
                ? null
                : txReport.RejectedTransactionReceipts.ToArray(),
            CurrentDifficulty = test.CurrentDifficulty,
            GasUsed = new UInt256(gasUsed),
            CurrentBaseFee = test.CurrentBaseFee,
            CurrentExcessBlobGas = hasBlobGas ? block.ExcessBlobGas : null,
            BlobGasUsed = hasBlobGas ? BlobGasCalculator.CalculateBlobGas(txReport.SuccessfulTransactions.ToArray()) : null,
            RequestsHash = block.RequestsHash,
            Requests = block.ExecutionRequests,
        };

        T8nExecutionResult t8NExecutionResult = new()
        {
            PostState = postState,
            TransactionsRlp = Rlp.Encode(txReport.SuccessfulTransactions.ToArray()).Bytes,
            Accounts = CollectAccounts(stateProvider, stateWrites),
        };

        return t8NExecutionResult;
    }

    private static T8nReceipt[] BuildReceipts(Block block, IReleaseSpec spec, List<Transaction> transactions, TxReceipt[] receipts)
    {
        UInt256? blobGasPrice = BlobGasCalculator.TryCalculateFeePerBlobGas(block.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas)
            ? feePerBlobGas
            : null;
        ulong logIndex = 0;
        T8nReceipt[] result = new T8nReceipt[receipts.Length];

        for (int i = 0; i < receipts.Length; i++)
        {
            TxReceipt receipt = receipts[i];
            LogEntry[] logs = receipt.Logs ?? [];
            T8nLog[] t8nLogs = new T8nLog[logs.Length];
            for (int j = 0; j < logs.Length; j++)
            {
                t8nLogs[j] = new T8nLog
                {
                    Address = logs[j].Address,
                    Topics = logs[j].Topics,
                    Data = logs[j].Data,
                    BlockNumber = block.Number,
                    TransactionHash = receipt.TxHash!,
                    TransactionIndex = (ulong)i,
                    BlockHash = PlaceholderBlockHash,
                    BlockTimestamp = block.Timestamp,
                    LogIndex = logIndex++,
                };
            }

            ulong blobGasUsed = BlobGasCalculator.CalculateBlobGas(transactions[i]);
            result[i] = new T8nReceipt
            {
                Type = receipt.TxType == TxType.Legacy ? null : receipt.TxType,
                Root = receipt.PostTransactionState?.BytesToArray() ?? [],
                Status = receipt.StatusCode,
                CumulativeGasUsed = receipt.GasUsedTotal,
                LogsBloom = receipt.Bloom,
                Logs = t8nLogs,
                TransactionHash = receipt.TxHash!,
                ContractAddress = receipt.ContractAddress ?? Address.Zero,
                GasUsed = receipt.GasUsed,
                BlobGasUsed = blobGasUsed == 0 ? null : blobGasUsed,
                BlobGasPrice = blobGasUsed == 0 ? null : blobGasPrice,
                BlockHash = PlaceholderBlockHash,
                BlockNumber = block.Number,
                TransactionIndex = (ulong)i,
            };
        }

        return result;
    }

    private static Dictionary<Address, AccountState> CollectAccounts(IWorldState stateProvider, StateWriteRecorder stateWrites)
    {
        Dictionary<Address, AccountState> accounts = [];
        foreach ((AddressAsKey address, HashSet<UInt256> slots) in stateWrites.Writes)
        {
            if (!stateProvider.AccountExists(address)) continue;

            stateProvider.TryGetAccount(address, out AccountStruct account);

            Dictionary<UInt256, byte[]> storage = [];
            foreach (UInt256 slot in slots)
            {
                stateProvider.Get(new StorageCell(address, slot), out UInt256 value);
                if (!value.IsZero) storage[slot] = value.ToBigEndian();
            }

            accounts[address] = new AccountState
            {
                Nonce = (ulong)account.Nonce,
                Balance = account.Balance,
                Code = stateProvider.GetCode(address).AsArray(),
                Storage = storage,
            };
        }

        return accounts;
    }
}
