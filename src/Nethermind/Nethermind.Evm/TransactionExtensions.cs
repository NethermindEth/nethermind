// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class TransactionExtensions
    {
        public static Address? GetRecipient(this Transaction tx, in UInt256 nonce) =>
            tx.To is not null
                ? tx.To
                : tx.IsSystem()
                    ? tx.SenderAddress
                    : ContractAddress.From(tx.SenderAddress, nonce > 0 ? nonce - 1 : nonce);

        public static TxGasInfo GetGasInfo(this Transaction tx, bool is1559Enabled, BlockHeader header)
        {
            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(is1559Enabled, header.BaseFeePerGas);

            if (tx.SupportsBlobs)
            {
                if (header.ExcessBlobGas is null)
                {
                    throw new ArgumentException($"Block that contains Shard Blob Transactions should have {nameof(header.ExcessBlobGas)} set.", nameof(header.ExcessBlobGas));
                }

                if (!BlobGasCalculator.TryCalculateBlobGasPricePerUnit(header, out UInt256 blobGasPrice))
                {
                    throw new OverflowException("Blob gas price calculation led to overflow.");
                }
                ulong blobGas = BlobGasCalculator.CalculateBlobGas(tx);

                return new(effectiveGasPrice, blobGasPrice, blobGas);
            }

            return new(effectiveGasPrice, null, null);
        }
    }

    public struct TxGasInfo
    {
        public TxGasInfo() { }

        public TxGasInfo(UInt256? effectiveGasPrice, UInt256? blobGasPrice, ulong? blobGasUsed)
        {
            EffectiveGasPrice = effectiveGasPrice;
            BlobGasPrice = blobGasPrice;
            BlobGasUsed = blobGasUsed;
        }

        public TxGasInfo(UInt256? effectiveGasPrice)
        {
            EffectiveGasPrice = effectiveGasPrice;
        }

        public UInt256? EffectiveGasPrice { get; private set; }
        public UInt256? BlobGasPrice { get; private set; }
        public ulong? BlobGasUsed { get; private set; }
    }
}
