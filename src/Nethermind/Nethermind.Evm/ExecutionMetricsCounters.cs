// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// Per-transaction accumulator for the per-opcode execution counters, flushed once into the global
/// <see cref="Metrics"/> at transaction end.
/// </summary>
/// <remarks>
/// A virtual machine instance executes one transaction at a time on one thread, so increments are plain
/// non-atomic adds — replacing the per-opcode cross-thread <see cref="System.Threading.Interlocked"/>
/// contention (shared by every prewarm worker) with a single atomic add per counter per transaction.
/// Increments mirror the <see cref="ExecutionMetricsFlag"/> gating of their globals.
/// </remarks>
internal struct ExecutionMetricsCounters
{
    public long SLoad;
    public long SStore;
    public long StorageDeleted;
    public long Calls;
    public long EmptyCalls;
    public long Creates;
    public long SelfDestructs;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementSLoad()
    {
        if (ExecutionMetricsFlag.IsActive) SLoad++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementSStore()
    {
        if (ExecutionMetricsFlag.IsActive) SStore++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStorageDeleted()
    {
        if (ExecutionMetricsFlag.IsActive) StorageDeleted++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementCalls()
    {
        if (ExecutionMetricsFlag.IsActive) Calls++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementEmptyCalls()
    {
        if (ExecutionMetricsFlag.IsActive) EmptyCalls++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementCreates()
    {
        if (ExecutionMetricsFlag.IsActive) Creates++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementSelfDestructs()
    {
        if (ExecutionMetricsFlag.IsActive) SelfDestructs++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        if (SLoad != 0) Metrics.AddSLoadOpcode(SLoad);
        if (SStore != 0) Metrics.AddSStoreOpcode(SStore);
        if (StorageDeleted != 0) Metrics.AddStorageDeleted(StorageDeleted);
        if (Calls != 0) Metrics.AddCalls(Calls);
        if (EmptyCalls != 0) Metrics.AddEmptyCalls(EmptyCalls);
        if (Creates != 0) Metrics.AddCreates(Creates);
        if (SelfDestructs != 0) Metrics.AddSelfDestructs(SelfDestructs);
    }
}
