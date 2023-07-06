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
                if (!DataGasCalculator.TryCalculateDataGasPricePerUnit(header, out UInt256 dataGasPrice))
                {
                    throw new ArgumentException(nameof(dataGasPrice));
                }
                ulong dataGas = DataGasCalculator.CalculateDataGas(tx);

                return new(effectiveGasPrice, dataGasPrice, dataGas);
            }

            return new(effectiveGasPrice, null, null);
        }
    }

    public struct TxGasInfo
    {
        public TxGasInfo() { }

        public TxGasInfo(UInt256? effectiveGasPrice, UInt256? dataGasPrice, ulong? dataGasUsed)
        {
            EffectiveGasPrice = effectiveGasPrice;
            DataGasPrice = dataGasPrice;
            DataGasUsed = dataGasUsed;
        }

        public TxGasInfo(UInt256? effectiveGasPrice)
        {
            EffectiveGasPrice = effectiveGasPrice;
        }

        public UInt256? EffectiveGasPrice { get; private set; }
        public UInt256? DataGasPrice { get; private set; }
        public ulong? DataGasUsed { get; private set; }
    }
}
