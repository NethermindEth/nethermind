// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class Deposit
    {
        public Keccak Id { get; }
        public uint Units { get; }
        public UInt256 Value { get; }
        public uint ExpiryTime { get; }

        public Deposit(Keccak id, uint units, uint expiryTime, UInt256 value)
        {
            Id = id;
            Units = units;
            Value = value;
            ExpiryTime = expiryTime;
        }
    }
}
