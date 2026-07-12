// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using System.Text.Json.Serialization;
using Nethermind.Core.ExecutionRequest;

namespace Nethermind.Merge.Plugin.Data;

public interface IExecutionPayloadFactory<out TExecutionPayload> where TExecutionPayload : ExecutionPayload
{
    static abstract TExecutionPayload Create(Block block);
}

/// <summary>
/// Represents an object mapping the <c>ExecutionPayload</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayload : IForkValidator, IExecutionPayloadParams, IExecutionPayloadFactory<ExecutionPayload>
{
    public UInt256 BaseFeePerGas { get; set; }

    public Hash256 BlockHash { get; set; } = Keccak.Zero;

    public ulong BlockNumber { get; set; }

    public byte[] ExtraData { get; set; } = [];

    public Address FeeRecipient { get; set; } = Address.Zero;

    public ulong GasLimit { get; set; }

    public ulong GasUsed { get; set; }

    public Bloom LogsBloom { get; set; } = Bloom.Empty;

    public Hash256 ParentHash { get; set; } = Keccak.Zero;

    public Hash256 PrevRandao { get; set; } = Keccak.Zero;

    public Hash256 ReceiptsRoot { get; set; } = Keccak.Zero;

    public Hash256 StateRoot { get; set; } = Keccak.Zero;

    public ulong Timestamp { get; set; }

    protected byte[][] _encodedTransactions = [];

    /// <summary>
    /// Gets or sets an array of RLP-encoded transaction where each item is a byte list (data)
    /// representing <c>TransactionType || TransactionPayload</c> or <c>LegacyTransaction</c> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-2718">EIP-2718</see>.
    /// </summary>
    [JsonConverter(typeof(TransactionsByteArrayArrayConverter))]
    public byte[][] Transactions
    {
        get => _encodedTransactions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _encodedTransactions = value;
            _transactions = null;
        }
    }

    /// <summary>
    /// Gets or sets a collection of <see cref="Withdrawal"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    public Withdrawal[]? Withdrawals { get; set; }


    /// <summary>
    /// Gets or sets a collection of <see cref="ExecutionRequest"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    [JsonIgnore]
    public virtual byte[][]? ExecutionRequests { get; set; }


    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? BlobGasUsed { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? ExcessBlobGas { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.BlockAccessList"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7928">EIP-7928</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual byte[]? BlockAccessList { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.SlotNumber"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? SlotNumber { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ParentBeaconBlockRoot"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4788">EIP-4788</see>.
    /// </summary>
    [JsonIgnore]
    public Hash256? ParentBeaconBlockRoot { get; set; }

    public static ExecutionPayload Create(Block block) => Create<ExecutionPayload>(block);

    protected static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayload, new()
    {
        TExecutionPayload executionPayload = new()
        {
            BlockHash = block.Hash!,
            ParentHash = block.ParentHash!,
            FeeRecipient = block.Beneficiary!,
            StateRoot = block.StateRoot!,
            BlockNumber = block.Number,
            GasLimit = block.GasLimit,
            GasUsed = block.GasUsed,
            ReceiptsRoot = block.ReceiptsRoot!,
            LogsBloom = block.Bloom!,
            PrevRandao = block.MixHash ?? Keccak.Zero,
            ExtraData = block.ExtraData!,
            Timestamp = block.Timestamp,
            BaseFeePerGas = block.BaseFeePerGas,
            Withdrawals = block.Withdrawals,
        };
        executionPayload.SetTransactions(block.Transactions);
        return executionPayload;
    }

    /// <summary>
    /// Creates the execution block from payload.
    /// </summary>
    /// <param name="totalDifficulty">A total difficulty of the block.</param>
    /// <returns>The decoded execution block or a decoding error.</returns>
    public virtual Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        byte[][] encodedTransactions = Transactions;
        Task<Hash256>? txRootTask = encodedTransactions.Length >= MinTxsForParallelDecoding && Environment.ProcessorCount > 1
            ? Task.Run(() => TxTrie.CalculateRoot(encodedTransactions))
            : null;

        Result<Transaction[]> transactions = TryGetTransactions();
        if (transactions.IsError)
        {
            // Observe the root task so a throw cannot surface as an unobserved task exception.
            txRootTask?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return transactions.Error;
        }

        BlockHeader header = new(
            ParentHash,
            Keccak.OfAnEmptySequenceRlp,
            FeeRecipient,
            UInt256.Zero,
            BlockNumber,
            GasLimit,
            Timestamp,
            ExtraData)
        {
            Hash = BlockHash,
            ReceiptsRoot = ReceiptsRoot,
            StateRoot = StateRoot,
            Bloom = LogsBloom,
            GasUsed = GasUsed,
            BaseFeePerGas = BaseFeePerGas,
            Nonce = 0,
            MixHash = PrevRandao,
            Author = FeeRecipient,
            IsPostMerge = true,
            TotalDifficulty = totalDifficulty,
            TxRoot = txRootTask is not null ? txRootTask.GetAwaiter().GetResult() : TxTrie.CalculateRoot(encodedTransactions),
            WithdrawalsRoot = BuildWithdrawalsRoot(),
        };

        Block block = new(header, transactions.Data, Array.Empty<BlockHeader>(), Withdrawals)
        {
            EncodedTransactions = encodedTransactions
        };
        return block;
    }

    protected virtual Hash256? BuildWithdrawalsRoot() => Withdrawals is null ? null : new WithdrawalTrie(Withdrawals).RootHash;

    protected Transaction[]? _transactions = null;

    private const int MinTxsForParallelDecoding = 32;

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Result<Transaction[]> TryGetTransactions()
    {
        if (_transactions is not null) return _transactions;

        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null) return $"{nameof(Transaction)} decoder is not registered";

        byte[][] txData = Transactions;
        if (txData.Length >= MinTxsForParallelDecoding && TryDecodeTransactionsParallel(rlpDecoder, txData, out Transaction[] decoded))
        {
            return _transactions = decoded;
        }

        // Serial path doubles as the failure fallback: it reproduces the exact single-threaded
        // behavior, pinpointing the first invalid transaction.
        int i = 0;
        try
        {
            Transaction[] transactions = new Transaction[txData.Length];

            for (i = 0; i < transactions.Length; i++)
            {
                transactions[i] = DecodeTransaction(rlpDecoder, txData[i]);
            }

            return _transactions = transactions;
        }
        catch (RlpException e)
        {
            return $"Transaction {i} is not valid: {e.Message}";
        }
        catch (ArgumentException)
        {
            return $"Transaction {i} is not valid";
        }
    }

    private static Transaction DecodeTransaction(IRlpDecoder<Transaction> rlpDecoder, byte[] rlp)
    {
        RlpReader ctx = new(rlp);
        return rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
    }

    private static bool TryDecodeTransactionsParallel(IRlpDecoder<Transaction> rlpDecoder, byte[][] txData, out Transaction[] transactions)
    {
        Transaction[] decoded = new Transaction[txData.Length];
        bool[] failed = new bool[1];

        ParallelUnbalancedWork.For(
            0,
            txData.Length,
            ParallelUnbalancedWork.DefaultOptions,
            (rlpDecoder, txData, decoded, failed),
            static (i, state) =>
            {
                try
                {
                    state.decoded[i] = DecodeTransaction(state.rlpDecoder, state.txData[i]);
                }
                catch
                {
                    // Any failure defers to the serial fallback, which reproduces the exact
                    // single-threaded error behavior (first invalid index, exception surface).
                    Volatile.Write(ref state.failed[0], true);
                }

                return state;
            });

        transactions = decoded;
        return !Volatile.Read(ref failed[0]);
    }

    /// <summary>
    /// RLP-encodes and sets the transactions specified to <see cref="Transactions"/>.
    /// </summary>
    /// <param name="transactions">An array of transactions to encode.</param>
    public void SetTransactions(params Transaction[] transactions)
    {
        Transactions = transactions
            .Select(static t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();
        _transactions = transactions;
    }

    public override string ToString() => $"{BlockNumber} ({BlockHash.ToShortString()})";

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => this;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.IsEip4844Enabled)
        {
            error = "ExecutionPayloadV3 expected";
            return ValidationResult.Fail;
        }

        int actualVersion = GetExecutionPayloadVersion();

        error = actualVersion switch
        {
            1 when spec.WithdrawalsEnabled => "ExecutionPayloadV2 expected",
            > 1 when !spec.WithdrawalsEnabled => "ExecutionPayloadV1 expected",
            _ => actualVersion > version ? $"ExecutionPayloadV{version} expected" : null
        };

        return error is null ? ValidationResult.Success : ValidationResult.Fail;
    }

    protected virtual int GetExecutionPayloadVersion() => this switch
    {
        { BlockAccessList: not null } => 4,
        { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
        { Withdrawals: not null } => 2,
        _ => 1
    };

    public virtual bool ValidateFork(ISpecProvider specProvider) =>
        !specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;
}
