// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash}, Value: {Value}, To: {To}, Gas: {GasLimit}")]
    public class Transaction
    {
        public static ReadOnlySpan<byte> EofMagic => [0xEF, 0x00];
        public const byte MaxTxType = 0x7F;
        public const int BaseTxGasCost = 21000;

        public ulong? ChainId { get; set; }

        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType Type { get; set; }

        // Taiko Anchor transaction
        public bool IsAnchorTx { get; set; }

        // Optimism deposit transaction fields
        // SourceHash uniquely identifies the source of the deposit
        public Hash256? SourceHash { get; set; }
        // Mint is minted on L2, locked on L1, nil if no minting.
        public UInt256 Mint { get; set; }
        // Field indicating if this transaction is exempt from the L2 gas limit.
        public bool IsOPSystemTransaction { get; set; }

        public UInt256 Nonce { get; set; }
        public UInt256 GasPrice { get; set; }
        public UInt256? GasBottleneck { get; set; }
        public UInt256 MaxPriorityFeePerGas => GasPrice;
        public UInt256 DecodedMaxFeePerGas { get; set; }
        public UInt256 MaxFeePerGas => Supports1559 ? DecodedMaxFeePerGas : GasPrice;
        public bool SupportsAccessList => Type.SupportsAccessList();
        public bool Supports1559 => Type.Supports1559();
        public bool SupportsBlobs => Type.SupportsBlobs();
        public bool SupportsAuthorizationList => Type.SupportsAuthorizationList();
        public long GasLimit { get; set; }
        private long _spentGas;
        [JsonIgnore]
        public long SpentGas { get => _spentGas > 0 ? _spentGas : GasLimit; set => _spentGas = value; }
        public Address? To { get; set; }
        public UInt256 Value { get; set; }
        public Memory<byte>? Data { get; set; }
        public Address? SenderAddress { get; set; }
        public Signature? Signature { get; set; }
        public bool IsSigned => Signature is not null;
        public bool IsContractCreation => To is null;
        public bool IsEofContractCreation => IsContractCreation && (Data?.Span.StartsWith(EofMagic) ?? false);
        public bool IsLegacyContractCreation => IsContractCreation && !IsEofContractCreation;
        public bool IsMessageCall => To is not null;

        [MemberNotNullWhen(true, nameof(AuthorizationList))]
        public bool HasAuthorizationList =>
            Type == TxType.SetCode &&
            AuthorizationList is not null &&
            AuthorizationList.Length > 0;

        private Hash256? _hash;

        [JsonIgnore]
        internal bool IsHashCalculated => _hash is not null;
        internal Hash256 CalculateHashInternal()
        {
            Hash256? hash = _hash;
            if (hash is not null) return hash;

            lock (this)
            {
                hash = _hash;
                if (hash is not null) return hash;

                if (_preHash.Length > 0)
                {
                    _hash = hash = Keccak.Compute(_preHash.Span);
                    ClearPreHashInternal();
                }
            }

            return hash!;
        }

        public Hash256? Hash
        {
            get
            {
                Hash256? hash = _hash;
                if (hash is not null) return hash;

                return CalculateHashInternal();
            }
            set
            {
                lock (this)
                {
                    ClearPreHash();
                    _hash = value;
                }
            }
        }

        private Memory<byte> _preHash;
        private IMemoryOwner<byte>? _preHashMemoryOwner;
        public void SetPreHash(ReadOnlySpan<byte> transactionSequence)
        {
            lock (this)
            {
                SetPreHashNoLock(transactionSequence);
            }
        }

        public void SetPreHashNoLock(ReadOnlySpan<byte> transactionSequence)
        {
            // Used to delay hash generation, as may be filtered as having too low gas etc
            _hash = null;

            int size = transactionSequence.Length;
            _preHashMemoryOwner = MemoryPool<byte>.Shared.Rent(size);
            _preHash = _preHashMemoryOwner.Memory[..size];
            transactionSequence.CopyTo(_preHash.Span);
        }

        public void SetPreHashMemoryNoLock(Memory<byte> transactionSequence, IMemoryOwner<byte>? preHashMemoryOwner = null)
        {
            // Used to delay hash generation, as may be filtered as having too low gas etc
            _hash = null;
            _preHash = transactionSequence;
            _preHashMemoryOwner = preHashMemoryOwner;
        }

        public void ClearPreHash()
        {
            if (_preHash.Length > 0)
            {
                lock (this)
                {
                    ClearPreHashInternal();
                }
            }
        }

        private void ClearPreHashInternal()
        {
            if (_preHash.Length > 0)
            {
                _preHashMemoryOwner?.Dispose();
                _preHashMemoryOwner = null;
                _preHash = default;
            }
        }

        public UInt256 Timestamp { get; set; }

        public int DataLength => Data?.Length ?? 0;

        public AccessList? AccessList { get; set; } // eip2930

        public UInt256? MaxFeePerBlobGas { get; set; } // eip4844

        public byte[]?[]? BlobVersionedHashes { get; set; } // eip4844

        public object? NetworkWrapper { get; set; }

        /// <summary>
        /// List of EOA code authorizations.
        /// https://eips.ethereum.org/EIPS/eip-7702
        /// </summary>
        public AuthorizationTuple[]? AuthorizationList { get; set; }

        /// <summary>
        /// Service transactions are free. The field added to handle baseFee validation after 1559
        /// </summary>
        /// <remarks>Used for AuRa consensus.</remarks>
        public bool IsServiceTransaction { get; set; }

        /// <summary>
        /// In-memory only property, representing order of transactions going to TxPool.
        /// </summary>
        /// <remarks>Used for sorting in edge cases.</remarks>
        public ulong PoolIndex { get; set; }

        protected int? _size = null;

        /// <summary>
        /// Encoded transaction length
        /// </summary>
        public int GetLength(ITransactionSizeCalculator sizeCalculator, bool shouldCountBlobs)
        {
            return shouldCountBlobs
              ? _size ??= sizeCalculator.GetLength(this, true)
              : sizeCalculator.GetLength(this, false);
        }

        public string ToShortString()
        {
            string gasPriceString =
                Supports1559 ? $"maxPriorityFeePerGas: {MaxPriorityFeePerGas}, MaxFeePerGas: {MaxFeePerGas}" : $"gas price {GasPrice}";
            return $"[TX: hash {Hash} from {SenderAddress} to {To} with data {Data.AsArray()?.ToHexString()}, {gasPriceString} and limit {GasLimit}, nonce {Nonce}]";
        }

        public string ToString(string indent)
        {
            StringBuilder builder = new();
            builder.AppendLine($"{indent}Hash:      {Hash}");
            builder.AppendLine($"{indent}From:      {SenderAddress}");
            builder.AppendLine($"{indent}To:        {To}");
            builder.AppendLine($"{indent}TxType:    {Type}");
            if (Supports1559)
            {
                builder.AppendLine($"{indent}MaxPriorityFeePerGas: {MaxPriorityFeePerGas}");
                builder.AppendLine($"{indent}MaxFeePerGas: {MaxFeePerGas}");
            }
            else
            {
                builder.AppendLine($"{indent}Gas Price: {GasPrice}");
            }

            builder.AppendLine($"{indent}SourceHash: {SourceHash}");
            builder.AppendLine($"{indent}Mint:      {Mint}");
            builder.AppendLine($"{indent}OpSystem:  {IsOPSystemTransaction}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Nonce:     {Nonce}");
            builder.AppendLine($"{indent}Value:     {Value}");
            builder.AppendLine($"{indent}Data:      {(Data.AsArray() ?? []).ToHexString()}");
            builder.AppendLine($"{indent}Signature: {Signature?.Bytes.ToHexString()}");
            builder.AppendLine($"{indent}V:         {Signature?.V}");
            builder.AppendLine($"{indent}ChainId:   {Signature?.ChainId}");
            builder.AppendLine($"{indent}Timestamp: {Timestamp}");

            if (SupportsBlobs)
            {
                builder.AppendLine($"{indent}{nameof(MaxFeePerBlobGas)}: {MaxFeePerBlobGas}");
                builder.AppendLine($"{indent}{nameof(BlobVersionedHashes)}: {BlobVersionedHashes?.Length}");
            }

            return builder.ToString();
        }

        public override string ToString() => ToString(string.Empty);

        public bool MayHaveNetworkForm => Type is TxType.Blob;

        public class PoolPolicy : IPooledObjectPolicy<Transaction>
        {
            public Transaction Create()
            {
                return new Transaction();
            }

            public bool Return(Transaction obj)
            {
                obj.ClearPreHash();
                obj.Hash = default;
                obj.ChainId = default;
                obj.Type = default;
                obj.Nonce = default;
                obj.GasPrice = default;
                obj.GasBottleneck = default;
                obj.DecodedMaxFeePerGas = default;
                obj.GasLimit = default;
                obj.To = default;
                obj.Value = default;
                obj.Data = default;
                obj.SenderAddress = default;
                obj.Signature = default;
                obj.Timestamp = default;
                obj.AccessList = default;
                obj.MaxFeePerBlobGas = default;
                obj.BlobVersionedHashes = default;
                obj.NetworkWrapper = default;
                obj.IsServiceTransaction = default;
                obj.PoolIndex = default;
                obj._size = default;
                obj.AuthorizationList = default;

                return true;
            }
        }

        public void CopyTo(Transaction tx)
        {
            tx.ChainId = ChainId;
            tx.Type = Type;
            tx.SourceHash = SourceHash;
            tx.Mint = Mint;
            tx.IsOPSystemTransaction = IsOPSystemTransaction;
            tx.Nonce = Nonce;
            tx.GasPrice = GasPrice;
            tx.GasBottleneck = GasBottleneck;
            tx.DecodedMaxFeePerGas = DecodedMaxFeePerGas;
            tx.GasLimit = GasLimit;
            tx.To = To;
            tx.Value = Value;
            tx.Data = Data;
            tx.SenderAddress = SenderAddress;
            tx.Signature = Signature;
            tx.Timestamp = Timestamp;
            tx.AccessList = AccessList;
            tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
            tx.BlobVersionedHashes = BlobVersionedHashes;
            tx.NetworkWrapper = NetworkWrapper;
            tx.IsServiceTransaction = IsServiceTransaction;
            tx.PoolIndex = PoolIndex;
            tx._size = _size;
            tx.AuthorizationList = AuthorizationList;
        }
    }

    /// <summary>
    /// Transaction that is generated by the node to be included in future block. After included in the block can be handled as regular <see cref="Transaction"/>.
    /// </summary>
    public class GeneratedTransaction : Transaction { }

    /// <summary>
    /// System transaction that is to be executed by the node without including in the block.
    /// </summary>
    public class SystemTransaction : Transaction
    {
        private new const long GasLimit = 30_000_000L;
    }

    /// <summary>
    /// System call like transaction that is to be executed by the node without including in the block.
    /// </summary>
    public class SystemCall : Transaction
    {
    }

    /// <summary>
    /// Used inside Transaction::GetSize to calculate encoded transaction size
    /// </summary>
    /// <remarks>Created because of cyclic dependencies between Core and Rlp modules</remarks>
    public interface ITransactionSizeCalculator
    {
        int GetLength(Transaction tx, bool shouldCountBlobs = true);
    }

    /// <summary>
    /// Holds network form fields for <see cref="TxType.Blob" /> transactions
    /// </summary>
    public record class ShardBlobNetworkWrapper(byte[][] Blobs, byte[][] Commitments, byte[][] Proofs, ProofVersion Version);

    public enum ProofVersion : byte
    {
        V0 = 0x00,
        V1 = 0x01,
    }
}
