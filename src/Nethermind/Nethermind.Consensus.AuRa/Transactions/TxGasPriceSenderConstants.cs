// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public static class TxGasPriceSenderConstants
    {
        public static UInt256 DefaultGasPrice = 20_000_000ul;

        public const uint DefaultPercentMultiplier = 110;
    }
}
