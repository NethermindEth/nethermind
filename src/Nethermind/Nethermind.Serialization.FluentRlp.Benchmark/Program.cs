using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.FluentRlp.Generator;
using CurrentRlp = Nethermind.Serialization.Rlp;

// The namespace deliberately avoids nesting under `Nethermind.Serialization.Rlp`, which would shadow
// FluentRlp's `RlpReader`/`RlpWriter` with the identically-named types in that namespace.
namespace Nethermind.Serialization.FluentRlp.Benchmark;

[RlpSerializable(representation: RlpRepresentation.Newtype, length: Size)]
public record Address(byte[] Bytes)
{
    public const int Size = 20;
}

[RlpSerializable]
public record AccessList(List<(Address, List<UInt256>)> Addresses);

[MemoryDiagnoser]
public class CurrentFluentBenchmark
{
    private readonly Nethermind.Core.Eip2930.AccessList _current;
    private readonly AccessList _fluent;

    public CurrentFluentBenchmark()
    {
        _current = Benchmark.Current.BuildAccessList(new Random(42));
        _fluent = Benchmark.Fluent.BuildAccessList(new Random(42));
    }

    [Benchmark(Baseline = true)]
    public Nethermind.Core.Eip2930.AccessList Current()
    {
        CurrentRlp.Eip2930.AccessListDecoder decoder = CurrentRlp.Eip2930.AccessListDecoder.Instance;

        int length = decoder.GetLength(_current, CurrentRlp.RlpBehaviors.None);
        byte[] buffer = new byte[length];
        CurrentRlp.RlpWriter writer = new(buffer);
        decoder.Encode(ref writer, _current);

        return decoder.Decode(buffer)!;
    }

    [Benchmark]
    public AccessList Fluent()
    {
        byte[] encoded = Rlp.Write(_fluent, static (ref RlpWriter writer, AccessList value) => writer.Write(value));
        return Rlp.Read(encoded, static (scoped ref RlpReader reader) => reader.ReadAccessList());
    }
}

public static class Current
{
    private static Nethermind.Core.Address BuildAddress(Random rnd)
    {
        byte[] bytes = new byte[Core.Address.Size];
        rnd.NextBytes(bytes);
        return new Nethermind.Core.Address(bytes);
    }

    public static Nethermind.Core.Eip2930.AccessList BuildAccessList(Random rnd)
    {
        Nethermind.Core.Eip2930.AccessList.Builder builder = new();
        for (int i = 0; i < 1_000; i++)
        {
            builder.AddAddress(BuildAddress(rnd));
            int keyCount = rnd.Next(10);
            for (int j = 0; j < keyCount; j++)
            {
                byte[] bytes = new byte[32];
                rnd.NextBytes(bytes);
                builder.AddStorage(new UInt256(bytes));
            }
        }

        return builder.Build();
    }
}

public static class Fluent
{
    private static Address BuildAddress(Random rnd)
    {
        byte[] bytes = new byte[Address.Size];
        rnd.NextBytes(bytes);
        return new Address(bytes);
    }

    public static AccessList BuildAccessList(Random rnd)
    {
        List<(Address, List<UInt256>)> result = new(1_000);
        for (int i = 0; i < 1_000; i++)
        {
            Address address = BuildAddress(rnd);
            List<UInt256> keys = [];
            int keyCount = rnd.Next(10);
            for (int j = 0; j < keyCount; j++)
            {
                byte[] bytes = new byte[32];
                rnd.NextBytes(bytes);
                keys.Add(new UInt256(bytes));
            }

            result.Add((address, keys));
        }

        return new AccessList(result);
    }
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run(typeof(Program).Assembly, args: args);
}
