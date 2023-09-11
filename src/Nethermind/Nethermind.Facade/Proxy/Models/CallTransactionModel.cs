// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using static Nethermind.Core.Extensions.MemoryExtensions;

namespace Nethermind.Facade.Proxy.Models
{
    public class CallTransactionModel
    {
        public Address? From { get; set; } = Address.SystemUser;
        public Address? To { get; set; } = Address.Zero;
        public UInt256? Gas { get; set; } = 0;
        public UInt256? GasPrice { get; set; } = 0;
        public UInt256? Value { get; set; } = 0;
        public byte[]? Data { get; set; } = { };

        public static CallTransactionModel FromTransaction(Transaction transaction)
            => new()
            {
                From = transaction.SenderAddress,
                To = transaction.To,
                Data = transaction.Data.AsArray(),
                Value = transaction.Value,
                Gas = (UInt256)transaction.GasLimit,
                GasPrice = transaction.GasPrice
            };

        public Transaction GetTransaction()
        {
            if (Gas > long.MaxValue) throw new OverflowException("Gas value is too large to be converted to long we use.");

            From ??= Address.SystemUser;

            Transaction? result = new Transaction
            {
                SenderAddress = From,
                To = To,
                Data = Data,
                Value = Value.Value,
                GasLimit = (long)Gas,
                GasPrice = GasPrice.Value

            };

            result.Hash = result.CalculateHash();
            return result;
        }

    }
}
