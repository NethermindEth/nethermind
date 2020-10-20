using System.Security;
using System.Threading.Tasks;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public class BaselineTreeTrackerTests
    {
        [Test]
        public async Task On_adding_one_leaf_count_goes_up_to_1()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            BaselineTree baselineTree = BuildATree();
            new BaselineTreeTracker(TestItem.Addresses[0], baselineTree, testRpc.LogFinder, testRpc.BlockFinder, testRpc.BlockProcessor);
            await testRpc.AddBlock();
        }

        private BaselineTree BuildATree(IKeyValueStore keyValueStore = null)
        {
            return new ShaBaselineTree(keyValueStore ?? new MemDb(), new byte[] { }, 0);
        }
    }
}
