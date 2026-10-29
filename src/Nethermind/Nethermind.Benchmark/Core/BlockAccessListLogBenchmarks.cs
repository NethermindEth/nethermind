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
/// Records one synthetic block into <see cref="BlockAccessListLog"/> runs and merges them into a <see cref="MergedBlockAccessList"/>.
/// </summary>
/// <remarks>
/// The <c>Record</c> benchmark covers what the workers do per transaction, sealing included, and the <c>Merge</c> ones
/// the serial end-of-block work. The transactions are dealt round-robin to <see cref="Workers"/> logs, as the parallel
/// workers share a log across the transactions they run; a single log is the sequential path.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class BlockAccessListLogBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    private Address[] _addresses = null!;
    private UInt256[] _slots = null!;
    private BlockAccessIndexRecorder[] _recorders = null!;
    private BlockAccessListLog[] _sealedLogs = null!;
    private ReadOnlyBlockAccessList _suggested = null!;
    private readonly MergedBlockAccessList _merged = new();

    [Params(200, 1000)]
    public int TxCount { get; set; }

    [Params(4, 16)]
    public int SlotsPerTx { get; set; }

    [Params(1, 16)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _addresses = new Address[Math.Max(32, TxCount * 2)];
        for (int i = 0; i < _addresses.Length; i++)
        {
            byte[] addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            _addresses[i] = new Address(addressBytes);
        }

        _slots = new UInt256[Math.Max(SlotsPerTx * 8, 64)];
        Span<byte> slotBytes = stackalloc byte[32];
        for (int i = 0; i < _slots.Length; i++)
        {
            random.NextBytes(slotBytes);
            _slots[i] = new UInt256(slotBytes);
        }

        _recorders = new BlockAccessIndexRecorder[Workers];
        _sealedLogs = new BlockAccessListLog[Workers];
        for (int worker = 0; worker < Workers; worker++)
        {
            _recorders[worker] = new BlockAccessIndexRecorder();
            _sealedLogs[worker] = new BlockAccessListLog();
        }

        BlockAccessIndexRecorder sealing = new();
        for (int tx = 0; tx < TxCount; tx++)
        {
            sealing.Log = _sealedLogs[tx % Workers];
            sealing.Begin((uint)tx);
            RecordTransaction(sealing, tx);
        }
        foreach (BlockAccessListLog log in _sealedLogs) log.Seal();

        _suggested = BlockAccessListDecoder.Instance.Decode(Merge_LogsAndEncode())!;
    }

    [Benchmark(Baseline = true)]
    public void Record_Logs()
    {
        foreach (BlockAccessIndexRecorder recorder in _recorders) recorder.Log.Reset();
        for (int tx = 0; tx < TxCount; tx++)
        {
            BlockAccessIndexRecorder recorder = _recorders[tx % Workers];
            recorder.Begin((uint)tx);
            RecordTransaction(recorder, tx);
        }
        foreach (BlockAccessIndexRecorder recorder in _recorders) recorder.Log.Seal();
    }

    [Benchmark]
    public byte[] Merge_LogsAndEncode()
    {
        _merged.Merge(_sealedLogs);
        return MergedBlockAccessListEncoder.EncodeToBytes(_merged);
    }

    [Benchmark]
    public bool Merge_LogsAndMatch()
    {
        _merged.Merge(_sealedLogs);
        return _merged.Matches(_suggested, out _);
    }

    /// <summary>A value transfer plus a contract call that reads then writes its slots; every eighth call reverts.</summary>
    private void RecordTransaction(BlockAccessIndexRecorder recorder, int tx)
    {
        Address sender = _addresses[tx % _addresses.Length];
        Address recipient = _addresses[(tx + 1) % _addresses.Length];
        Address coinbase = _addresses[0];
        Address contract = _addresses[(tx * 3 + 7) % _addresses.Length];

        recorder.AddAccountRead(sender);
        recorder.AddBalanceChange(sender, 1_000, 900);
        recorder.AddNonceChange(sender, (ulong)tx + 1);
        recorder.AddBalanceChange(recipient, 100, 150);

        recorder.AddAccountRead(contract);
        int snapshot = recorder.TakeSnapshot();
        for (int s = 0; s < SlotsPerTx; s++)
        {
            StorageCell cell = new(contract, _slots[(tx * SlotsPerTx + s) % _slots.Length]);
            recorder.AddStorageRead(cell);
            recorder.AddStorageChange(cell, 0, (ulong)(tx + s + 1));
        }
        recorder.AddStorageRead(new StorageCell(contract, _slots[(tx + 1) % _slots.Length]));
        if (tx % 8 == 7) recorder.Restore(snapshot);

        recorder.AddBalanceChange(coinbase, (ulong)tx * 10, (ulong)tx * 10 + 10);
    }
}
