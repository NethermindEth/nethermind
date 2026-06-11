using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class TreeStoreBenchmark
    {
        static TreeStoreBenchmark() => _ = LimboLogs.Instance.GetClassLogger<TreeStoreBenchmark>(); // lazy-init

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

        private readonly IKeyValueStoreWithBatching _whateverDb = new MemDb();

        private readonly ILogManager _logManager = new OneLoggerLogManager(NullLogger.Instance);

        [Benchmark]
        public TrieStore Trie_committer_with_one_node()
        {
            TrieNode trieNode = new(NodeType.Leaf, new byte[7]);
            TreePath emptyPath = TreePath.Empty;
            trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);

            TrieStore treeStore = TestTrieStoreFactory.Build(_whateverDb, No.Pruning, No.Persistence, _logManager);
            using IBlockCommitter _ = treeStore.BeginBlockCommit(1234);
            using ICommitter committer = treeStore.GetTrieStore(null).BeginCommit(trieNode);
            committer.CommitNode(ref emptyPath, trieNode);
            return treeStore;
        }
    }
}
