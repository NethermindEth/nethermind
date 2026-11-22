// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class TransactionExtensions
    {
        [SkipLocalsInit]
        public static Address GetRecipient(this Transaction tx, in UInt256 nonce) =>
            tx.To ?? (tx.IsSystem()
                ? tx.SenderAddress!
                : ContractAddress.From(tx.SenderAddress, nonce > 0 ? nonce - 1 : nonce));

        public static TxGasInfo GetGasInfo(this Transaction tx, IReleaseSpec spec, BlockHeader header)
        {
            UInt256? feePerBlobGas = null;
            return tx.GetGasInfo(spec, header, ref feePerBlobGas);
        }

        /// <summary>
        /// As <see cref="GetGasInfo(Transaction, IReleaseSpec, BlockHeader)"/>, computing the fee per blob gas of
        /// <paramref name="header"/> at most once across the transactions of one block.
        /// </summary>
        public static TxGasInfo GetGasInfo(this Transaction tx, IReleaseSpec spec, BlockHeader header, ref UInt256? feePerBlobGas)
        {
            // EIP-8141: gate on blob presence, not the type-3-only SupportsBlobs, so frame-blob txs report blob gas.
            if (tx.CarriesBlobs)
            {
                feePerBlobGas ??= CalculateFeePerBlobGas(spec, header);
                ulong blobGas = BlobGasCalculator.CalculateBlobGas(tx);

                return new(CalculateEffectiveGasPrice(tx, spec, header, feePerBlobGas.Value), feePerBlobGas, blobGas);
            }

            return new(CalculateEffectiveGasPrice(tx, spec, header, UInt256.Zero), null, null);
        }

        /// <summary>The price per gas <paramref name="tx"/> pays; for an EIP-8141 frame transaction, what <c>GASPRICE</c> reads.</summary>
        private static UInt256 CalculateEffectiveGasPrice(Transaction tx, IReleaseSpec spec, BlockHeader header, in UInt256 feePerBlobGas) =>
            tx.SupportsFrames && FrameTxValidation.TryCalculateGasPrice(tx, spec, header.BaseFeePerGas, in feePerBlobGas, out UInt256 gasPrice)
                ? gasPrice
                : tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas);

        private static UInt256 CalculateFeePerBlobGas(IReleaseSpec spec, BlockHeader header)
        {
            if (header.ExcessBlobGas is null)
            {
                throw new ArgumentException($"Block that contains Shard Blob Transactions should have {nameof(header.ExcessBlobGas)} set.", nameof(header.ExcessBlobGas));
            }

            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(header, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas))
            {
                throw new OverflowException("Blob gas price calculation led to overflow.");
            }

            return feePerBlobGas;
        }
    }

    public readonly struct TxGasInfo
    {
        public TxGasInfo() { }

        public TxGasInfo(UInt256? effectiveGasPrice, UInt256? feePerBlobGas, ulong? blobGasUsed)
        {
            EffectiveGasPrice = effectiveGasPrice;
            BlobGasPrice = feePerBlobGas;
            BlobGasUsed = blobGasUsed;
        }

        public TxGasInfo(UInt256? effectiveGasPrice) => EffectiveGasPrice = effectiveGasPrice;

        public UInt256? EffectiveGasPrice { get; }
        public UInt256? BlobGasPrice { get; }
        public ulong? BlobGasUsed { get; }
    }
}
