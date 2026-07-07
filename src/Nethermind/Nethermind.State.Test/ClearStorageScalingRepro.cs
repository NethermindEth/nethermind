// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Temporary scaling measurement for the ClearStorage quadratic hypothesis (task 28 slow-block
/// signature): within one commit round, N touched cells across K contracts, then M contracts
/// destroyed — ClearStorage full-scans the round's cell dictionaries per destroy.
/// </summary>
[Explicit("measurement, not a test")]
public class ClearStorageScalingRepro
{
    [Test]
    public void Measure_sets_only_baseline()
    {
        // Isolates the Set(Zero) cost from ClearStorage internals: manually zero the same
        // cells ClearStorage would (10 per contract × 800 contracts) without calling it.
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        const int contractCount = 1_000;
        const int slots = 10;
        Address[] contracts = new Address[contractCount];
        byte[] value = Keccak.Compute("v").BytesToArray();
        for (int i = 0; i < contractCount; i++)
        {
            contracts[i] = Address.FromNumber(new UInt256((ulong)(0x10000 + i)));
            worldState.CreateAccount(contracts[i], 1);
            for (int s = 0; s < slots; s++)
                worldState.Set(new StorageCell(contracts[i], new UInt256((ulong)(s + 1))), value);
        }

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 800; i++)
        {
            for (int s = 0; s < slots; s++)
                worldState.Set(new StorageCell(contracts[i], new UInt256((ulong)(s + 1))), StorageTree.ZeroBytes);
        }

        sw.Stop();
        Console.WriteLine($"sets-only 800×{slots}: {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Test]
    public void Measure_scaling()
    {
        // (touched cells total, contracts, destroys)
        (int cells, int contracts, int destroys)[] grid =
        [
            (2_000, 200, 100),
            (5_000, 500, 200),
            (10_000, 1_000, 400),
            (10_000, 1_000, 800),
            (20_000, 2_000, 800),
            (10_000, 1_000, 1),
        ];

        foreach ((int cells, int contracts, int destroys) in grid)
        {
            double ms = RunScenario(cells, contracts, destroys, out double perDestroyUs);
            Console.WriteLine(
                $"cells={cells,6} contracts={contracts,5} destroys={destroys,4} " +
                $"=> ClearStorage total {ms,9:F2} ms ({perDestroyUs,8:F1} us/destroy)");
        }
    }

    private static double RunScenario(int totalCells, int contractCount, int destroyCount, out double perDestroyUs)
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);

        Address[] contracts = new Address[contractCount];
        for (int i = 0; i < contractCount; i++)
        {
            contracts[i] = Address.FromNumber(new UInt256((ulong)(0x10000 + i)));
            worldState.CreateAccount(contracts[i], 1);
        }

        // Touch cells round-robin across contracts within ONE commit round (one tx),
        // populating _intraBlockCache and _originalValues.
        int slotsPerContract = totalCells / contractCount;
        byte[] value = Keccak.Compute("v").BytesToArray();
        for (int i = 0; i < contractCount; i++)
        {
            for (int s = 0; s < slotsPerContract; s++)
            {
                worldState.Set(new StorageCell(contracts[i], new UInt256((ulong)(s + 1))), value);
            }
        }

        // Warm the code paths once with a single clear on a throwaway contract.
        Address warm = Address.FromNumber(new UInt256(0xFFFFF));
        worldState.CreateAccount(warm, 1);
        worldState.Set(new StorageCell(warm, UInt256.One), value);
        worldState.ClearStorage(warm);

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < destroyCount; i++)
        {
            worldState.ClearStorage(contracts[i]);
        }

        sw.Stop();
        perDestroyUs = sw.Elapsed.TotalMilliseconds * 1000.0 / destroyCount;
        return sw.Elapsed.TotalMilliseconds;
    }
}
