using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Db;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class FullDbOnTheRocksTests
    {
        private IFullDb _db;

        [SetUp]
        public void Initialize()
        {
            var path = Path.Combine(Path.GetTempPath(), "FullDbOnTheRocksTestsDb");
            _db = new FullDbOnTheRocks(path);
        }

        [Test]
        public void ReadWriteTest()
        {
            var key = Encoding.UTF8.GetBytes("testKey");
            var value = Encoding.UTF8.GetBytes("testValue");
            var key2 = Encoding.UTF8.GetBytes("testKey2");
            var value2 = Encoding.UTF8.GetBytes("testValue2");
            _db[key] = value;
            _db[key2] = value2;

            Assert.AreEqual(value, _db[key]);
            Assert.AreEqual(value2, _db[key2]);

            _db[key] = value2;
            _db[key2] = value;

            Assert.AreEqual(value2, _db[key]);
            Assert.AreEqual(value, _db[key2]);

            var keys = _db.Keys;
            var values = _db.Values;

            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(2, values.Count);

            Assert.Contains(key, keys.ToArray());
            Assert.Contains(key2, keys.ToArray());

            Assert.Contains(value, values.ToArray());
            Assert.Contains(value2, values.ToArray());
        }
    }
}