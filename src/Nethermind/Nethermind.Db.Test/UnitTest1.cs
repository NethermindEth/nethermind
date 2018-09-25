using Nethermind.Db.Config;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class DbOnTheRocksTests
    {
        [Test]
        public void Smoke_test()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new DbOnTheRocks("test", config);
            db[new byte[] {1, 2, 3}] = new byte[] {4, 5, 6};
            Assert.AreEqual(new byte[] {4, 5, 6}, db[new byte[] {1, 2, 3}]);
        }
    }
}