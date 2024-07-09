using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle.Custom;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Ethereum.Test.Base.T8NUtils;

public class T8NResult
{
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
    public Dictionary<Address, AccountState> Accounts { get; set; }
    public byte[] TransactionsRlp { get; set; }

    public static T8NResult ConstructT8NResult(WorldState stateProvider,
        Block block,
        GeneralStateTest test,
        T8NToolTracer tracer,
        ISpecProvider specProvider,
        BlockHeader header,
        TransactionExecutionReport transactionExecutionReport)
    {
        T8NResult t8NResult = new();
        Hash256 txRoot = TxTrie.CalculateRoot(transactionExecutionReport.SuccessfulTransactions.ToArray());
        IReceiptSpec receiptSpec = specProvider.GetSpec(header);
        Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec,
            transactionExecutionReport.SuccessfulTransactionReceipts.ToArray(),
            new ReceiptMessageDecoder());
        var logEntries = transactionExecutionReport.SuccessfulTransactionReceipts
            .SelectMany(receipt => receipt.Logs ?? Enumerable.Empty<LogEntry>())
            .ToArray();
        var bloom = new Bloom(logEntries);
        ulong gasUsed = 0;
        if (!tracer.TxReceipts.IsNullOrEmpty())
        {
            gasUsed = (ulong)tracer.LastReceipt.GasUsedTotal;
        }

        ulong? blobGasUsed = test.Fork.IsEip4844Enabled
            ? BlobGasCalculator.CalculateBlobGas(transactionExecutionReport.ValidTransactions.ToArray())
            : null;

        t8NResult.TxRoot = txRoot;
        t8NResult.ReceiptsRoot = receiptsRoot;
        t8NResult.LogsBloom = bloom;
        t8NResult.LogsHash = Keccak.Compute(Rlp.OfEmptySequence.Bytes);
        t8NResult.Receipts = transactionExecutionReport.SuccessfulTransactionReceipts.ToArray();
        t8NResult.Rejected = transactionExecutionReport.RejectedTransactionReceipts.IsNullOrEmpty()
            ? null
            : transactionExecutionReport.RejectedTransactionReceipts.ToArray();
        t8NResult.CurrentDifficulty = test.CurrentDifficulty;
        t8NResult.GasUsed = new UInt256(gasUsed);
        t8NResult.CurrentBaseFee = test.CurrentBaseFee;
        t8NResult.WithdrawalsRoot = block.WithdrawalsRoot;
        t8NResult.CurrentExcessBlobGas = header.ExcessBlobGas;
        t8NResult.BlobGasUsed = blobGasUsed;

        var accounts = test.Pre.Keys.ToDictionary(address => address,
            address => GetAccountState(address, stateProvider, tracer.storages));
        foreach (Ommer ommer in test.Ommers)
        {
            accounts.Add(ommer.Address, GetAccountState(ommer.Address, stateProvider, tracer.storages));
        }

        if (header.Beneficiary != null)
        {
            accounts.Add(header.Beneficiary, GetAccountState(header.Beneficiary, stateProvider, tracer.storages));
        }

        t8NResult.Accounts = accounts.Where(account => !account.Value.IsEmptyAccount()).ToDictionary();
        t8NResult.TransactionsRlp =
            Rlp.Encode(transactionExecutionReport.SuccessfulTransactions.ToArray()).Bytes;

        return t8NResult;
    }

    private static AccountState GetAccountState(Address address, WorldState stateProvider, Dictionary<Address, Dictionary<UInt256, byte[]>> storages)
    {
        var account = stateProvider.GetAccount(address);
        var code = stateProvider.GetCode(address);
        var accountState = new AccountState
        {
            Nonce = account.Nonce,
            Balance = account.Balance,
            Code = code.Length == 0 ? null : code
        };

        if (storages.TryGetValue(address, out var storage))
        {
            accountState.Storage = storage;
        }

        return accountState;
    }
}