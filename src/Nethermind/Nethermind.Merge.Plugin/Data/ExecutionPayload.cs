// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Nethermind.Core;
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
    public UInt256 BaseFeePerGas { get; set => field = Bind(value, PayloadFields.BaseFeePerGas); }

    public Hash256 BlockHash { get; set => field = Bind(value, PayloadFields.BlockHash); } = Keccak.Zero;

    public ulong BlockNumber { get; set => field = Bind(value, PayloadFields.BlockNumber); }

    public byte[] ExtraData { get; set => field = Bind(value, PayloadFields.ExtraData); } = [];

    public Address FeeRecipient { get; set => field = Bind(value, PayloadFields.FeeRecipient); } = Address.Zero;

    public ulong GasLimit { get; set => field = Bind(value, PayloadFields.GasLimit); }

    public ulong GasUsed { get; set => field = Bind(value, PayloadFields.GasUsed); }

    public Bloom LogsBloom { get; set => field = Bind(value, PayloadFields.LogsBloom); } = Bloom.Empty;

    public Hash256 ParentHash { get; set => field = Bind(value, PayloadFields.ParentHash); } = Keccak.Zero;

    public Hash256 PrevRandao { get; set => field = Bind(value, PayloadFields.PrevRandao); } = Keccak.Zero;

    public Hash256 ReceiptsRoot { get; set => field = Bind(value, PayloadFields.ReceiptsRoot); } = Keccak.Zero;

    public Hash256 StateRoot { get; set => field = Bind(value, PayloadFields.StateRoot); } = Keccak.Zero;

    public ulong Timestamp { get; set => field = Bind(value, PayloadFields.Timestamp); }

    /// <summary>
    /// Payload fields tracked for presence, with the set each <c>ExecutionPayloadV*</c> structure requires.
    /// </summary>
    [Flags]
    private protected enum PayloadFields : uint
    {
        None = 0,
        BaseFeePerGas = 1 << 0,
        BlockHash = 1 << 1,
        BlockNumber = 1 << 2,
        ExtraData = 1 << 3,
        FeeRecipient = 1 << 4,
        GasLimit = 1 << 5,
        GasUsed = 1 << 6,
        LogsBloom = 1 << 7,
        ParentHash = 1 << 8,
        PrevRandao = 1 << 9,
        ReceiptsRoot = 1 << 10,
        StateRoot = 1 << 11,
        Timestamp = 1 << 12,
        Transactions = 1 << 13,
        Withdrawals = 1 << 14,
        BlobGasUsed = 1 << 15,
        ExcessBlobGas = 1 << 16,
        BlockAccessList = 1 << 17,
        SlotNumber = 1 << 18,
        V1 = (1 << 14) - 1,
        V2 = V1 | Withdrawals,
        V3 = V2 | BlobGasUsed | ExcessBlobGas,
        V4 = V3 | BlockAccessList | SlotNumber
    }

    // Defaults hide an omitted JSON key once binding is done, so setters record presence while it runs.
    private protected PayloadFields _unboundFields;

    private protected T Bind<T>(T value, PayloadFields payloadField)
    {
        _unboundFields = value is null ? _unboundFields | payloadField : _unboundFields & ~payloadField;
        return value;
    }

    /// <summary>Whether the request omitted a required field or sent it as <c>null</c>.</summary>
    /// <remarks>Only payload types that arm presence tracking on deserialization report a field.</remarks>
    internal bool HasUnboundField => _unboundFields != PayloadFields.None;

    /// <summary>The JSON key of the first unbound field; call only when <see cref="HasUnboundField"/> is <c>true</c>.</summary>
    internal string UnboundFieldName =>
        JsonNamingPolicy.CamelCase.ConvertName(((PayloadFields)(1u << BitOperations.TrailingZeroCount((uint)_unboundFields))).ToString());

    /// <summary>The invalid-params message for the first unbound field; call only when <see cref="HasUnboundField"/> is <c>true</c>.</summary>
    internal string UnboundFieldError => $"{UnboundFieldName} must be set";

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
            _unboundFields &= ~PayloadFields.Transactions;
            _encodedTransactions = value;
            _transactions = null;
            TransactionsRoot = null;
        }
    }

    /// <summary>
    /// Gets or sets a collection of <see cref="Withdrawal"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    public Withdrawal[]? Withdrawals { get; set => field = Bind(value, PayloadFields.Withdrawals); }


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
            TxRoot = TransactionsRoot ??= TxTrie.CalculateRoot(encodedTransactions),
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

    /// <summary>Whether <see cref="TryGetTransactions"/> has already decoded <see cref="Transactions"/>.</summary>
    internal bool HasDecodedTransactions => _transactions is not null;

    internal Hash256? TransactionsRoot { get; set; }

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
        !specProvider.GetSpec(BlockNumber, Timestamp).IsCancunEnabled;

    /// <summary>Whether this payload may arrive on the given <c>engine_newPayload</c> version.</summary>
    public virtual bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion) =>
        !specProvider.GetSpec(BlockNumber, Timestamp).IsCancunEnabled;
}
