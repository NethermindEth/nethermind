// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DepositForRpc
    {
        public Keccak? Id { get; set; }
        public uint? Units { get; set; }
        public UInt256? Value { get; set; }
        public uint? ExpiryTime { get; set; }

        public DepositForRpc()
        {
        }

        public DepositForRpc(Deposit deposit)
        {
            Id = deposit.Id;
            Units = deposit.Units;
            Value = deposit.Value;
            ExpiryTime = deposit.ExpiryTime;
        }
    }
}
