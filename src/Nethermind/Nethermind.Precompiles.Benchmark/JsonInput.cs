// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global
namespace Nethermind.Precompiles.Benchmark
{
    public class JsonInput
    {
        public byte[]? Input { get; set; }
        public byte[]? Expected { get; set; }
        public string? Name { get; set; }
        public long Gas { get; set; }
        public bool NoBenchmark { get; set; }
    }
}
