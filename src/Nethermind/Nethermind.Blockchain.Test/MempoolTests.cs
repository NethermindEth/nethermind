using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class MempoolTests
    {
        private IEthereumSigner _ethereumSigner;
        private ISpecProvider _specProvider;
        private ILogManager _logManager;
        private Block _genesisBlock;
        private IMempool _mempool;
        private IBlockTree _remoteBlockTree;

        [SetUp]
        public void Setup()
        {
            _logManager = NullLogManager.Instance;
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _specProvider = MainNetSpecProvider.Instance;
            _ethereumSigner = new EthereumSigner(_specProvider, _logManager);
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(0).TestObject;
        }

        [Test]
        public void Test()
        {
            _mempool = new Mempool(_logManager, iterationsLimit: 10, iterationsInterval: 10);
            var peers = GetPeers();
            var transactions = GetTransactions(peers);

            foreach (var peer in peers)
            {
                _mempool.AddPeer(peer);
            }

            foreach (var transaction in transactions)
            {
                _mempool.AddTransaction(transaction);
            }
        }

        private ISynchronizationPeer[] GetPeers(int limit = 100)
        {
            var peers = new List<ISynchronizationPeer>();
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
            {
                for (var i = 0; i < limit; i++)
                {
                    rng.GetBytes(bytes);
                    var privateKey = new PrivateKey(bytes);
                    peers.Add(GetPeer(privateKey.PublicKey));
                }
            }

            return peers.ToArray();
        }

        private ISynchronizationPeer GetPeer(PublicKey publicKey)
            => new SynchronizationPeerMock(_remoteBlockTree, publicKey);

        private Transaction[] GetTransactions(IEnumerable<ISynchronizationPeer> peers, int transactionsPerPeer = 10)
        {
            var transactions = new List<Transaction>();
            var addressNumber = 0;
            foreach (var peer in peers)
            {
                addressNumber++;
                for (var i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(Address.FromNumber(addressNumber), peer.NodeId.PublicKey));
                    addressNumber++;
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(Address to, PublicKey deliveredBy) => Build.A.Transaction
            .WithGasLimit(1000)
            .WithGasPrice(1)
            .To(to)
            .Signed(_ethereumSigner, TestObject.PrivateKeyA, 1)
            .DeliveredBy(deliveredBy)
            .TestObject;
    }
}