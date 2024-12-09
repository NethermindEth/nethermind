// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Evm.T8n;

public class T8nTest(IReleaseSpec spec, ISpecProvider specProvider, Address currentCoinbase)
{
    public IReleaseSpec Spec { get; set; } = spec;
    public ISpecProvider SpecProvider { get; set; } = specProvider;
    public Dictionary<Address, AccountState> Alloc { get; set; } = [];
    public Transaction[] Transactions { get; set; } = [];

    public Address CurrentCoinbase { get; set; } = currentCoinbase;
    public UInt256? CurrentDifficulty { get; set; }
    public UInt256? CurrentBaseFee { get; set; }
    public long CurrentGasLimit { get; set; }
    public long CurrentNumber { get; set; }
    public ulong CurrentTimestamp { get; set; }
    public Hash256? CurrentRandom { get; set; }
    public ulong? CurrentExcessBlobGas { get; set; }
    public UInt256? ParentBlobGasUsed { get; set; }
    public UInt256? ParentExcessBlobGas { get; set; }
    public Withdrawal[]? Withdrawals { get; set; }
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

    private BlockHeader ConstructBlockHeader()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(CurrentTimestamp)
            .WithGasLimit(CurrentGasLimit)
            .WithDifficulty(CurrentDifficulty ?? UInt256.Zero)
            .WithBeneficiary(CurrentCoinbase)
            .WithNumber(CurrentNumber)
            .TestObject;

        if (CurrentRandom is not null) header.MixHash = CurrentRandom;
        if (CurrentBaseFee.HasValue) header.BaseFeePerGas = CurrentBaseFee.Value;
        if (ParentExcessBlobGas.HasValue && ParentBlobGasUsed.HasValue)
        {
            BlockHeader parentHeader = Build.A.BlockHeader
                .WithExcessBlobGas((ulong)ParentExcessBlobGas)
                .WithBlobGasUsed((ulong)ParentBlobGasUsed)
                .TestObject;
            header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parentHeader, Spec);
        }
        header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(Transactions);
        header.IsPostMerge = Spec is Paris;
        header.Hash = header.CalculateHash();

        return header;
    }

    public Block ConstructBlock()
    {

        BlockHeader[] uncles = Ommers
            .Select(ommer => Build.A.BlockHeader
                .WithNumber(CurrentNumber - ommer.Delta)
                .WithBeneficiary(ommer.Address)
                .TestObject)
            .ToArray();

        BlockHeader header = ConstructBlockHeader();
        return Build.A.Block
            .WithHeader(header)
            .WithTransactions(Transactions)
            .WithWithdrawals(Withdrawals)
            .WithUncles(uncles)
            .TestObject;
    }
}
