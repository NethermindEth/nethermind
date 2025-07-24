using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.FluentRlp.Generator;

namespace Nethermind.Serialization.Rlp.Benchmark;

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
        var decoder = Eip2930.AccessListDecoder.Instance;

        var length = decoder.GetLength(_current, RlpBehaviors.None);
        var stream = new RlpStream(length);
        decoder.Encode(stream, _current);

        stream.Reset();

        var decoded = decoder.Decode(stream);
        return decoded!;
    }

    [Benchmark]
    public AccessList Fluent()
    {
        var decoded = FluentRlp.Rlp.Write(_fluent, (ref RlpWriter writer, AccessList value) => writer.Write(value));
        var encoded = FluentRlp.Rlp.Read(decoded, (scoped ref RlpReader reader) => reader.ReadAccessList());

        return encoded;
    }
}

public static class Current
{
    private static Nethermind.Core.Address BuildAddress(Random rnd)
    {
        var bytes = new byte[Core.Address.Size];
        rnd.NextBytes(bytes);
        return new Nethermind.Core.Address(bytes);
    }

    public static Nethermind.Core.Eip2930.AccessList BuildAccessList(Random rnd)
    {
        var builder = new Nethermind.Core.Eip2930.AccessList.Builder();
        for (int i = 0; i < 1_000; i++)
        {
            builder.AddAddress(BuildAddress(rnd));
            var keyCount = rnd.Next(10);
            for (int j = 0; j < keyCount; j++)
            {
                var bytes = new byte[32];
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
        var bytes = new byte[Address.Size];
        rnd.NextBytes(bytes);
        return new Address(bytes);
    }

    public static AccessList BuildAccessList(Random rnd)
    {
        var result = new List<(Address, List<UInt256>)>(1_000);
        for (int i = 0; i < 1_000; i++)
        {
            Address address = BuildAddress(rnd);
            List<UInt256> keys = [];
            var keyCount = rnd.Next(10);
            for (int j = 0; j < keyCount; j++)
            {
                var bytes = new byte[32];
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
    public static void Main()
    {
        BenchmarkRunner.Run(typeof(Program).Assembly);
    }
}
