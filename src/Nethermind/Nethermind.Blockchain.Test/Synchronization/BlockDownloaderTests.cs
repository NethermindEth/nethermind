//using System;
//using System.Collections.Generic;
//using System.Configuration;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;
//using Nethermind.Blockchain.Synchronization;
//using Nethermind.Core;
//using Nethermind.Core.Crypto;
//using Nethermind.Core.Logging;
//using Nethermind.Core.Test.Builders;
//using Nethermind.Dirichlet.Numerics;
//using Nethermind.Stats;
//using Nethermind.Stats.Model;
//using NSubstitute;
//using NUnit.Framework;
//
//namespace Nethermind.Blockchain.Test.Synchronization
//{
//    [TestFixture]
//    public class BlockDownloaderTests
//    {
//        private List<(ISyncServer SyncManager, IBlockTree Tree)> _peers;
//        private (ISyncServer SyncManager, IBlockTree Tree) _localPeer;
//        private (ISyncServer SyncManager, IBlockTree Tree) _remotePeer1;
//        private (ISyncServer SyncManager, IBlockTree Tree) _remotePeer2;
//        private (ISyncServer SyncManager, IBlockTree Tree) _remotePeer3;
//        private static Block _genesis = Build.A.Block.Genesis.TestObject;
//
//        private IBlockDownloader _blockDownloader;
//        private INodeStatsManager _stats;
//        private IEthSyncPeerPool _pool;
//        private IBlockTree _blockTree;
//
//        [SetUp]
//        public void SetUp()
//        {
//            _blockTree = Substitute.For<IBlockTree>();
//            _stats = Substitute.For<INodeStatsManager>();
//            _pool = new EthSyncPeerPool(_blockTree, _stats, new SyncConfig(), LimboLogs.Instance);
//            _blockDownloader = new BlockDownloader(_pool, LimboLogs.Instance);
//
//            _peers = new List<(ISyncServer, IBlockTree)>();
//
//            _localPeer = _peers[0];
//            _remotePeer1 = _peers[1];
//            _remotePeer2 = _peers[2];
//            _remotePeer3 = _peers[3];
//
//            int chainLength = 10000;
//            var headBlock = _genesis;
//            for (int i = 0; i < chainLength; i++)
//            {
//                var block = Build.A.Block.WithParent(headBlock).WithTotalDifficulty((headBlock.TotalDifficulty ?? 0) + 1).TestObject;
//                _remotePeer1.Tree.SuggestBlock(block);
//                headBlock = block;
//            }
//        }
//    }
//}