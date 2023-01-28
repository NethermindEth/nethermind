// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models
{
    public class CallTransactionModel
    {
        public Address From { get; set; }
        public Address To { get; set; }
        public UInt256 Gas { get; set; }
        public UInt256 GasPrice { get; set; }
        public UInt256 Value { get; set; }
        public byte[] Data { get; set; }

        public static CallTransactionModel FromTransaction(Transaction transaction)
            => new()
            {
                From = transaction.SenderAddress,
                To = transaction.To,
                Data = transaction.Data,
                Value = transaction.Value,
                Gas = (UInt256)transaction.GasLimit,
                GasPrice = transaction.GasPrice
            };
    }
}
