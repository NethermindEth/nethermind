// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs;

namespace Evm.t8n.JsonTypes;

public class T8NTest(IReleaseSpec spec, ISpecProvider specProvider)
{
    public IReleaseSpec Spec { get; set; } = spec;
    public ISpecProvider SpecProvider { get; set; } = specProvider;
    public Address? CurrentCoinbase { get; set; }
    public UInt256? CurrentDifficulty { get; set; }

    public UInt256? CurrentBaseFee { get; set; }
    public long CurrentGasLimit { get; set; }
    public long CurrentNumber { get; set; }
    public ulong CurrentTimestamp { get; set; }
    public Hash256? PreviousHash { get; set; }
    public Dictionary<Address, AccountState> Alloc { get; set; } = [];
    public Hash256? PostHash { get; set; }
    public Transaction[] Transactions { get; set; } = [];
    public Hash256? CurrentRandom { get; set; }
    public Hash256? CurrentBeaconRoot { get; set; }
    public Hash256? CurrentWithdrawalsRoot { get; set; }
    public ulong? CurrentExcessBlobGas { get; set; }
    public UInt256? ParentBlobGasUsed { get; set; }
    public UInt256? ParentExcessBlobGas { get; set; }

    public Withdrawal[] Withdrawals { get; set; } = [];
    public ulong ParentTimestamp { get; set; }
    public UInt256? ParentDifficulty { get; set; }
    public Hash256? ParentUncleHash { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public UInt256? ParentBaseFee { get; set; }
    public long ParentGasUsed { get; set; }
    public long ParentGasLimit { get; set; }
    public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
    public Ommer[] Ommers { get; set; } = [];
    public ulong StateChainId { get; set; } = MainnetSpecProvider.Instance.ChainId;
    public GethTraceOptions GethTraceOptions { get; set; } = GethTraceOptions.Default;
    public bool IsTraceEnabled { get; set; } = false;
}
