// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash}, Value: {Value}, To: {To}, Gas: {GasLimit}")]
    public class Transaction
    {
        public const int BaseTxGasCost = 21000;

        public ulong? ChainId { get; set; }

        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType Type { get; set; }

        public UInt256 Nonce { get; set; }
        public UInt256 GasPrice { get; set; }
        public UInt256? GasBottleneck { get; set; }
        public UInt256 MaxPriorityFeePerGas => GasPrice;
        public UInt256 DecodedMaxFeePerGas { get; set; }
        public UInt256 MaxFeePerGas => Supports1559 ? DecodedMaxFeePerGas : GasPrice;
        public bool SupportsAccessList => Type >= TxType.AccessList;
        public bool Supports1559 => Type >= TxType.EIP1559;
        public bool SupportsBlobs => Type == TxType.Blob;
        public long GasLimit { get; set; }
        public Address? To { get; set; }
        public UInt256 Value { get; set; }
        public Memory<byte>? Data { get; set; }
        public Address? SenderAddress { get; set; }
        public Signature? Signature { get; set; }
        public bool IsSigned => Signature is not null;
        public bool IsContractCreation => To is null;
        public bool IsMessageCall => To is not null;

        private Keccak? _hash;
        public Keccak? Hash
        {
            get
            {
                if (_hash is not null) return _hash;

                lock (this)
                {
                    if (_hash is not null) return _hash;

                    if (_preHash.Length > 0)
                    {
                        _hash = Keccak.Compute(_preHash.Span);
                        ClearPreHashInternal();
                    }
                }

                return _hash;
            }
            set
            {
                ClearPreHash();
                _hash = value;
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
        /// Service transactions are free. The field added to handle baseFee validation after 1559
        /// </summary>
        /// <remarks>Used for AuRa consensus.</remarks>
        public bool IsServiceTransaction { get; set; }

        /// <summary>
        /// In-memory only property, representing order of transactions going to TxPool.
        /// </summary>
        /// <remarks>Used for sorting in edge cases.</remarks>
        public ulong PoolIndex { get; set; }

        private int? _size = null;
        /// <summary>
        /// Encoded transaction length
        /// </summary>
        public int GetLength(ITransactionSizeCalculator sizeCalculator)
        {
            return _size ??= sizeCalculator.GetLength(this);
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
            if (Supports1559)
            {
                builder.AppendLine($"{indent}MaxPriorityFeePerGas: {MaxPriorityFeePerGas}");
                builder.AppendLine($"{indent}MaxFeePerGas: {MaxFeePerGas}");
            }
            else
            {
                builder.AppendLine($"{indent}Gas Price: {GasPrice}");
            }

            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Nonce:     {Nonce}");
            builder.AppendLine($"{indent}Value:     {Value}");
            builder.AppendLine($"{indent}Data:      {(Data.AsArray() ?? Array.Empty<byte>()).ToHexString()}");
            builder.AppendLine($"{indent}Signature: {(Signature?.Bytes ?? Array.Empty<byte>()).ToHexString()}");
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

                return true;
            }
        }
    }

    /// <summary>
    /// Transaction that is generated by the node to be included in future block. After included in the block can be handled as regular <see cref="Transaction"/>.
    /// </summary>
    public class GeneratedTransaction : Transaction { }

    /// <summary>
    /// System transaction that is to be executed by the node without including in the block.
    /// </summary>
    public class SystemTransaction : Transaction { }

    /// <summary>
    /// Used inside Transaction::GetSize to calculate encoded transaction size
    /// </summary>
    /// <remarks>Created because of cyclic dependencies between Core and Rlp modules</remarks>
    public interface ITransactionSizeCalculator
    {
        int GetLength(Transaction tx);
    }

    /// <summary>
    /// Holds network form fields for <see cref="TxType.Blob" /> transactions
    /// </summary>
    public class ShardBlobNetworkWrapper
    {
        public ShardBlobNetworkWrapper(byte[][] blobs, byte[][] commitments, byte[][] proofs)
        {
            Blobs = blobs;
            Commitments = commitments;
            Proofs = proofs;
        }

        public byte[][] Commitments { get; set; }
        public byte[][] Blobs { get; set; }
        public byte[][] Proofs { get; set; }
    }
}
