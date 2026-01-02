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

        private IKeyValueStore _whateverDb = new MemDb();

        private ILogManager _logManager = new OneLoggerLogManager(NullLogger.Instance);

        [Benchmark]
        public TrieStore Trie_committer_with_one_node()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown); // 56B

            ITrieNodeCache trieNodeCache = new TrieNodeCache(_logManager);
            TrieStore treeStore = new TrieStore(
                trieNodeCache,
                _whateverDb,
                new DepthAndMemoryBased(128, 1.MB()),
                No.Persistence,
                _logManager);
            treeStore.CommitOneNode(1234, new NodeCommitInfo(trieNode));
            return treeStore;
        }
    }
}
