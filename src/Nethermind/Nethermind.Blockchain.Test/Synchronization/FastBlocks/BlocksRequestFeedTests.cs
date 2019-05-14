using Nethermind.Blockchain.Synchronization.FastBlocks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.FastBlocks
{
    [TestFixture]
    public class BlocksRequestFeedTests
    {
        [Test]
        public void Test()
        {
            BlockTree blockTree = new BlockTree(); 
            BlocksRequestFeed feed = new BlocksRequestFeed();
            feed.PrepareRequest()
        }
    }
}