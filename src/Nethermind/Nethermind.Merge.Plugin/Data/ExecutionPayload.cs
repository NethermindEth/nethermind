// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
    /// <remarks>Decoded transactions borrow these buffers. Replace the property to change transactions;
    /// do not mutate buffers after decoding or starting root computation.</remarks>
    [JsonConverter(typeof(TransactionsByteArrayArrayConverter))]
    public byte[][] Transactions
    {
        get => _encodedTransactions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _encodedTransactions = value;
            _transactions = null;
            _txRoot = null;
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

    /// <summary>
    /// Gets or sets <see cref="InclusionListTransactions"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    [JsonIgnore]
    public virtual byte[][]? InclusionListTransactions { get; set; }

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
        // Repeats the check inside StartTxRootComputation so the guest build never reaches the call
        // and carries no work item for it.
        TxRootComputation? txRoot = RuntimeInformation.IsSingleProcessor ? null : StartTxRootComputation();

        Result<Transaction[]> transactions = TryGetTransactions();
        if (transactions.IsError)
        {
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
            TxRoot = txRoot is not null ? txRoot.GetResult() : TxTrie.CalculateRoot(encodedTransactions),
            WithdrawalsRoot = BuildWithdrawalsRoot(),
        };

        Block block = new(header, transactions.Data, Array.Empty<BlockHeader>(), Withdrawals)
        {
            EncodedTransactions = encodedTransactions
        };
        return block;
    }

    protected virtual Hash256? BuildWithdrawalsRoot() => Withdrawals is null ? null : WithdrawalTrie.CalculateRoot(Withdrawals);

    protected Transaction[]? _transactions = null;

    private TxRootComputation? _txRoot;

    private const int MinTxsForParallelDecoding = 32;

    /// <summary>
    /// Starts computing the transactions-trie root in the background, letting callers overlap it
    /// with serial work that precedes <see cref="TryGetBlock"/> (which consumes the computation).
    /// </summary>
    /// <remarks>
    /// The work item is queued rather than awaited: <see cref="TxRootComputation.GetResult"/> computes the root
    /// itself when the pool has not started it, so a pool still busy with the previous block delays nothing.
    /// Not thread-safe: concurrent calls, or a concurrent <see cref="Transactions"/> assignment,
    /// race the memoized computation. Callers must invoke both sequentially per payload instance.
    /// </remarks>
    /// <returns>
    /// The started computation, or <c>null</c> when the transaction count makes inline computation cheaper.
    /// </returns>
    internal TxRootComputation? StartTxRootComputation()
    {
        if (_txRoot is not null) return _txRoot;

        byte[][] encodedTransactions = _encodedTransactions;
        if (encodedTransactions.Length < MinTxsForParallelDecoding || RuntimeInformation.IsSingleProcessor) return null;

        TxRootComputation computation = _txRoot = new(encodedTransactions);
        // Queued to the global side of the pool, not this thread's local queue: a local item is only stolen once
        // the workers run dry, which is the case this exists to cover.
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), computation, preferLocal: false);
        return computation;
    }

    /// <summary>The transactions-trie root of one payload, computed once by whichever thread reaches it first.</summary>
    /// <remarks>
    /// Queued on the thread pool so it overlaps the serial work that precedes <see cref="TryGetBlock"/>, and claimed
    /// by the consumer when the pool has not picked it up yet. The root costs less than a wait for a queued work item
    /// does on a pool still busy with the previous block, and the thread that waits is the engine API's request
    /// thread: whoever arrives first does the work, the other blocks only for as long as it takes.
    /// Nested rather than a file of its own because the stateless executor compiles this file by link, and a new
    /// file would have to be listed in that project too.
    /// </remarks>
    /// <param name="encodedTransactions">The payload's transactions, which must not be mutated once this is started.</param>
    internal sealed class TxRootComputation(byte[][] encodedTransactions)
    {
        private readonly Lock _lock = new();
        private Hash256? _root;

        /// <summary>Computes the root, or returns at once when another thread has already computed it.</summary>
        /// <remarks>A thread that arrives while another is computing blocks until that one is done.</remarks>
        internal void Run()
        {
            if (Volatile.Read(ref _root) is not null) return;

            lock (_lock)
            {
                _root ??= TxTrie.CalculateRoot(encodedTransactions);
            }
        }

        /// <summary>The root, computed here when no thread has started it.</summary>
        internal Hash256 GetResult()
        {
            Run();
            return _root!;
        }
    }

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Result<Transaction[]> TryGetTransactions()
    {
        if (_transactions is not null) return _transactions;

        TransactionDecodingResult res = TxsDecoder.DecodeTxsBorrowingBuffers(Transactions, skipErrors: false);
        if (res.Error is not null) return res.Error;
        return _transactions = res.Transactions;
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

    /// <inheritdoc/>
    /// <remarks>Answers for the getPayloadV1 shape only. Not virtual: subclasses gate newPayload through
    /// <see cref="ValidateForkOnNewPayload"/> instead.</remarks>
    public bool ValidateFork(ISpecProvider specProvider) =>
        !specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;

    /// <summary>Whether this payload may arrive on the given <c>engine_newPayload</c> version.</summary>
    public virtual bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion) =>
        !specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;
}
