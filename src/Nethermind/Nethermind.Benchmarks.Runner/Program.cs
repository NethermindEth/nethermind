using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Runner;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<SpanZeroingBenchmark>();
    }
}

public class SpanZeroingBenchmark
{
    private static readonly Address Address = new Address("0xA4B05FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");

    [Benchmark(Baseline = true)]
    public ValueHash256 ToHashWithSkipLocals()
    {
        return Implementations.ToHashWithSkipLocals(Address);
    }

    [Benchmark]
    public ValueHash256 ToHashWithoutSkipLocals()
    {
        return Implementations.ToHashWithoutSkipLocals(Address);
    }

    [Benchmark]
    public ValueHash256 ToHashBensSpecial()
    {
        return Implementations.ToHashBensSpecial(Address);
    }
}

public static class Implementations
{
    private const int ZeroedPrefixSize = Hash256.Size - Address.Size;

    [SkipLocalsInit]
    public static ValueHash256 ToHashWithSkipLocals(Address address)
    {
        Span<byte> addressBytes = stackalloc byte[Hash256.Size];
        addressBytes[..ZeroedPrefixSize].Clear();
        address.Bytes.CopyTo(addressBytes[ZeroedPrefixSize..]);
        return new ValueHash256(addressBytes);
    }

    public static ValueHash256 ToHashWithoutSkipLocals(Address address)
    {
        Span<byte> addressBytes = stackalloc byte[Hash256.Size];
        address.Bytes.CopyTo(addressBytes[ZeroedPrefixSize..]);
        return new ValueHash256(addressBytes);
    }

    [SkipLocalsInit]
    public static ValueHash256 ToHashBensSpecial(Address address)
    {
        ref byte value = ref MemoryMarshal.GetArrayDataReference(address.Bytes);

        Unsafe.SkipInit(out ValueHash256 result);
        ref byte bytes = ref Unsafe.As<ValueHash256, byte>(ref result);

        // First 4+8 bytes are zero, zero 16 bytes to maximize write combining
        Unsafe.As<byte, Vector128<byte>>(ref bytes) = default;

        // 20 bytes which is uint+Vector128
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes, sizeof(uint) + sizeof(ulong)))
            = Unsafe.As<byte, uint>(ref value);

        Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref bytes, sizeof(ulong) + sizeof(ulong)))
            = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref value, sizeof(uint)));

        return result;
    }
}
