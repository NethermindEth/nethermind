// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class TransactionExtensions
    {
        public static Address GetRecipient(this Transaction tx, in UInt256 nonce) =>
            tx.To ?? (tx.IsSystem()
                ? tx.SenderAddress!
                : ContractAddress.From(tx.SenderAddress, nonce > 0 ? nonce - 1 : nonce));

        public static TxGasInfo GetGasInfo(this Transaction tx, IReleaseSpec spec, BlockHeader header)
        {
            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas);

            // EIP-8141: report blob gas from blob presence, not the type-3-only SupportsBlobs, so a
            // blob-carrying frame tx's receipt shows blobGasUsed/blobGasPrice.
            if (tx.GetBlobCount() > 0)
            {
                if (header.ExcessBlobGas is null)
                {
                    throw new ArgumentException($"Block that contains Shard Blob Transactions should have {nameof(header.ExcessBlobGas)} set.", nameof(header.ExcessBlobGas));
                }

                if (!BlobGasCalculator.TryCalculateFeePerBlobGas(header, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas))
                {
                    throw new OverflowException("Blob gas price calculation led to overflow.");
                }
                ulong blobGas = BlobGasCalculator.CalculateBlobGas(tx);

                return new(effectiveGasPrice, feePerBlobGas, blobGas);
            }

            return new(effectiveGasPrice, null, null);
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
