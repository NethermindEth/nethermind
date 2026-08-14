// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmarks the per-tx merge and end-of-block encode path of <see cref="GeneratedBlockAccessList"/>.
/// Covers EIP-7928 BAL construction: many small per-tx contributions merged into a block-level
/// aggregate, then RLP-encoded (encoder boundary sorts slots/addresses lazily via
/// <c>GetSortedAccountChanges</c> / <c>GetSortedStorageChanges</c>).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class BlockAccessListBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    private Address[] _addresses = null!;
    private UInt256[] _slots = null!;
    private GeneratedBlockAccessList _preBuiltBal = null!;

    [Params(50, 200)]
    public int TxCount { get; set; }

    [Params(4, 16)]
    public int SlotsPerTx { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);

        // Pre-generate the pool of addresses and storage keys the synthetic txs touch.
        int addressPool = Math.Max(32, TxCount * 2);
        _addresses = new Address[addressPool];
        for (int i = 0; i < addressPool; i++)
        {
            byte[] addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            _addresses[i] = new Address(addressBytes);
        }

        int slotPool = Math.Max(SlotsPerTx * 8, 64);
        _slots = new UInt256[slotPool];
        Span<byte> slotBytes = stackalloc byte[32];
        for (int i = 0; i < slotPool; i++)
        {
            random.NextBytes(slotBytes);
            _slots[i] = new UInt256(slotBytes);
        }

        _preBuiltBal = BuildBal();
    }

    [Benchmark]
    public GeneratedBlockAccessList BuildAndMerge() => BuildBal();

    [Benchmark]
    public byte[] EncodeToBytes() => BlockAccessListDecoder.EncodeToBytes(_preBuiltBal);

    [Benchmark]
    public byte[] BuildMergeAndEncode() => BlockAccessListDecoder.EncodeToBytes(BuildBal());

    private GeneratedBlockAccessList BuildBal()
    {
        GeneratedBlockAccessList bal = new();
        Address[] addresses = _addresses;
        UInt256[] slots = _slots;
        int txCount = TxCount;
        int slotsPerTx = SlotsPerTx;

        for (int tx = 0; tx < txCount; tx++)
        {
            BlockAccessListAtIndex slice = new() { Index = (uint)tx };

            // Each tx touches a small rolling window of accounts so the merge sees plenty of
            // already-seen addresses (the realistic case where many tx hit the same account).
            int senderIdx = tx % addresses.Length;
            int recipientIdx = (tx + 1) % addresses.Length;

            slice.AddBalanceChange(addresses[senderIdx], 100, 90);
            slice.AddBalanceChange(addresses[recipientIdx], 100, 110);
            slice.AddNonceChange(addresses[senderIdx], (ulong)(tx + 1));

            // Storage churn on a third account — the "contract" being called.
            int contractIdx = (tx * 3 + 7) % addresses.Length;
            Address contract = addresses[contractIdx];
            for (int s = 0; s < slotsPerTx; s++)
            {
                UInt256 slot = slots[(tx * slotsPerTx + s) % slots.Length];
                slice.AddStorageChange(contract, slot, before: 0, after: (ulong)(tx + s + 1));
            }
            // Mix in a read to exercise the storage-read collection too.
            slice.AddStorageRead(contract, slots[(tx + 1) % slots.Length]);

            bal.Merge(slice);
        }

        return bal;
    }
}
