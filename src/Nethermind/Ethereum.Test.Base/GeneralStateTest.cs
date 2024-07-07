// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class GeneralStateTest : IEthereumTest
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public IReleaseSpec? Fork { get; set; }
        public string? ForkName { get; set; }
        public Address? CurrentCoinbase { get; set; }
        public UInt256 CurrentDifficulty { get; set; }

        public UInt256? CurrentBaseFee { get; set; }
        public long CurrentGasLimit { get; set; }
        public long CurrentNumber { get; set; }
        public ulong CurrentTimestamp { get; set; }
        public Hash256? PreviousHash { get; set; }
        public Dictionary<Address, AccountState> Pre { get; set; }
        public Hash256? PostHash { get; set; }
        public Hash256? PostReceiptsRoot { get; set; }
        public string? LoadFailure { get; set; }
        public Transaction[] Transactions { get; set; } = [];
        public Hash256? CurrentRandom { get; set; }
        public Hash256? CurrentBeaconRoot { get; set; }
        public Hash256? CurrentWithdrawalsRoot { get; set; }
        public ulong? CurrentExcessBlobGas { get; set; }
        public UInt256? ParentBlobGasUsed { get; set; }
        public UInt256? ParentExcessBlobGas { get; set; }

        public Withdrawal[]? Withdrawals { get; set; }
        public ulong ParentTimestamp { get; set; } // optional?
        public UInt256? ParentDifficulty { get; set; }
        public Hash256? ParentUncleHash { get; set; }
        public Hash256? ParentBeaconBlockRoot { get; set; }
        public UInt256? ParentBaseFee { get; set; }
        public long ParentGasUsed { get; set; }
        public long ParentGasLimit { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
        public Ommer[] Ommers { get; set; } = [];

        public override string ToString()
        {
            return $"{Path.GetFileName(Category)}.{Name}_{ForkName}";
        }
    }
}
