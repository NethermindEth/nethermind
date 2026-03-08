// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Compares two approaches for counting zero/non-zero bytes in access list storage keys:
///   Span — serialize UInt256 via ToBigEndian into a stackalloc byte[32], then count zeros over the span
///   Swar — borrow-safe SWAR directly on the four ulong limbs, no memory write
/// </summary>
[MemoryDiagnoser]
public class AccessListTokensBenchmarks
{
    // EIP-7623 token weight for non-zero bytes
    private const int NonZeroMultiplier = 4;

    private Transaction _smallTx = null!;   // 1 address, 0 storage keys
    private Transaction _mediumTx = null!;  // 10 addresses, 5 storage keys each
    private Transaction _largeTx = null!;   // 50 addresses, 20 storage keys each

    [GlobalSetup]
    public void GlobalSetup()
    {
        _smallTx = BuildTransaction(addresses: 1, keysPerAddress: 0);
        _mediumTx = BuildTransaction(addresses: 10, keysPerAddress: 5);
        _largeTx = BuildTransaction(addresses: 50, keysPerAddress: 20);
    }

    [Benchmark(Baseline = true, Description = "Span — small (1 addr, 0 keys)")]
    public long Span_Small() => CountTokensViaSpan(_smallTx);

    [Benchmark(Description = "Swar — small (1 addr, 0 keys)")]
    public long Swar_Small() => CountTokensViaSwar(_smallTx);

    [Benchmark(Description = "Span — medium (10 addr, 5 keys)")]
    public long Span_Medium() => CountTokensViaSpan(_mediumTx);

    [Benchmark(Description = "Swar — medium (10 addr, 5 keys)")]
    public long Swar_Medium() => CountTokensViaSwar(_mediumTx);

    [Benchmark(Description = "Span — large (50 addr, 20 keys)")]
    public long Span_Large() => CountTokensViaSpan(_largeTx);

    [Benchmark(Description = "Swar — large (50 addr, 20 keys)")]
    public long Swar_Large() => CountTokensViaSwar(_largeTx);

    // ── baseline: ToBigEndian + CountZeros on Span<byte> ────────────────────────

    private static long CountTokensViaSpan(Transaction transaction)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is null) return 0L;

        long tokens = 0;
        Span<byte> keyBytes = stackalloc byte[Nethermind.Core.Extensions.UInt256Extensions.ByteSize];
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            ReadOnlySpan<byte> addressBytes = address.Bytes;
            int addressZeros = addressBytes.CountZeros();
            tokens += addressZeros + (addressBytes.Length - addressZeros) * NonZeroMultiplier;

            foreach (UInt256 key in storageKeys)
            {
                key.ToBigEndian(keyBytes);
                int keyZeros = ((ReadOnlySpan<byte>)keyBytes).CountZeros();
                tokens += keyZeros + (Nethermind.Core.Extensions.UInt256Extensions.ByteSize - keyZeros) * NonZeroMultiplier;
            }
        }
        return tokens;
    }

    // ── borrow-safe SWAR: production formula in UInt256Extensions ────────────────

    private static long CountTokensViaSwar(Transaction transaction)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is null) return 0L;

        long tokens = 0;

        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
        {
            ReadOnlySpan<byte> addressBytes = address.Bytes;
            int addressZeros = addressBytes.CountZeros();
            tokens += addressZeros + (addressBytes.Length - addressZeros) * NonZeroMultiplier;

            foreach (UInt256 key in storageKeys)
            {
                int keyZeros = key.CountZeroBytes();
                tokens += keyZeros + (Nethermind.Core.Extensions.UInt256Extensions.ByteSize - keyZeros) * NonZeroMultiplier;
            }
        }

        return tokens;
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private static Transaction BuildTransaction(int addresses, int keysPerAddress)
    {
        AccessList.Builder builder = new();
        for (int i = 0; i < addresses; i++)
        {
            byte[] bytes = new byte[20];
            // Vary bytes so we get a realistic mix of zero and non-zero values
            bytes[^1] = (byte)(i & 0xFF);
            bytes[^2] = (byte)((i >> 8) & 0xFF);
            bytes[10] = 0xAB;
            builder.AddAddress(new Address(bytes));

            for (int j = 0; j < keysPerAddress; j++)
            {
                // Mix of sparse (many zeros) and dense keys
                builder.AddStorage(new UInt256((ulong)j * 0x0001000200030004UL));
            }
        }
        return new Transaction { AccessList = builder.Build() };
    }
}
