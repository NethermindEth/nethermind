using System;
using System.Runtime.CompilerServices;
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
}

public static class Implementations
{
    private const int ZeroedPrefixSize = Hash256.Size - Address.Size;

    [SkipLocalsInit]
    public static ValueHash256 ToHashWithSkipLocals(Address address)
    {
        Span<byte> addressBytes = stackalloc byte[Hash256.Size];
        addressBytes[ZeroedPrefixSize..].Clear();
        address.Bytes.CopyTo(addressBytes[ZeroedPrefixSize..]);
        return new ValueHash256(addressBytes);
    }

    public static ValueHash256 ToHashWithoutSkipLocals(Address address)
    {
        Span<byte> addressBytes = stackalloc byte[Hash256.Size];
        address.Bytes.CopyTo(addressBytes[ZeroedPrefixSize..]);
        return new ValueHash256(addressBytes);
    }
}
