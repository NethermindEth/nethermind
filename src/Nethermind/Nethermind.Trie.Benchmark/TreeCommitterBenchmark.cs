using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class TreeStoreBenchmark
    {
        static TreeStoreBenchmark()
        {
            _ = LimboLogs.Instance.GetClassLogger(); // lazy-init
        }

        // public readonly struct Param
        // {
        //     public Param(byte[] bytes)
        //     {
        //         Bytes = bytes;
        //     }
        //     
        //     public byte[] Bytes { get; }
        //
        //     public override string ToString()
        //     {
        //         return $"bytes[{Bytes.Length.ToString().PadLeft(4, '0')}]";
        //     }
        // }
        //
        // public IEnumerable<Param> Inputs 
        // {
        //     get
        //     {
        //         yield return new Param(new byte[0]);
        //         yield return new Param(new byte[32]);
        //         yield return new Param(new byte[64]);
        //         yield return new Param(new byte[96]);
        //         yield return new Param(new byte[128]);
        //         yield return new Param(new byte[1024]);
        //         yield return new Param(new byte[2048]);
        //     }
        // }
        //
        // [ParamsSource(nameof(Inputs))]
        // public Param Input { get; set; }

        // Benchmark disabled: TrieStore API has changed significantly since this was written.
        // ITrieNodeCache, TrieNodeCache, DepthAndMemoryBased, NodeCommitInfo, CommitOneNode
        // no longer exist in their original form.
    }
}
