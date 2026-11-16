// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;

namespace Nethermind.Benchmarks.Core;

[MemoryDiagnoser]
public class HeaderHashValidationBenchmarks
{
    private static readonly byte[] EncodedEmptyBlockAccessList = [0xc0];

    private BlockHeader _header = null!;
    private Block _blockWithAccessList = null!;

    [GlobalSetup]
    public void Setup()
    {
        _header = Build.A.BlockHeader
            .WithParentHash(TestItem.KeccakA)
            .WithBeneficiary(TestItem.AddressA)
            .WithStateRoot(TestItem.KeccakB)
            .WithTransactionsRoot(TestItem.KeccakC)
            .WithReceiptsRoot(TestItem.KeccakD)
            .WithBloom(new Bloom(Enumerable.Repeat((byte)0x5a, Bloom.ByteLength).ToArray()))
            .WithDifficulty(0)
            .WithNumber(23_000_000)
            .WithGasLimit(45_000_000)
            .WithGasUsed(21_345_678)
            .WithTimestamp(1_760_000_000)
            .WithExtraDataHex("0x6265617665726275696c642e6f7267")
            .WithMixHash(TestItem.KeccakE)
            .WithNonce(0)
            .WithBaseFee(1_234_567_890)
            .WithWithdrawalsRoot(TestItem.KeccakF)
            .WithBlobGasUsed(786_432)
            .WithExcessBlobGas(12_345_678)
            .WithParentBeaconBlockRoot(TestItem.KeccakG)
            .WithRequestsHash(TestItem.KeccakH)
            .TestObject;
        _header.Hash = _header.CalculateHash();

        BlockHeader balHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(Keccak.Compute(EncodedEmptyBlockAccessList))
            .TestObject;
        _blockWithAccessList = new Block(balHeader, new BlockBody(), new ReadOnlyBlockAccessList())
        {
            EncodedBlockAccessList = EncodedEmptyBlockAccessList
        };
    }

    [Benchmark]
    public bool ValidateHash() => HeaderValidator.ValidateHash(_header);

    [Benchmark]
    public bool ValidateHashWithActual() => HeaderValidator.ValidateHash(_header, out _);

    [Benchmark]
    public bool ValidateBlockAccessListHash() => BlockValidator.ValidateBlockLevelAccessListHashMatches(_blockWithAccessList, out _);
}
