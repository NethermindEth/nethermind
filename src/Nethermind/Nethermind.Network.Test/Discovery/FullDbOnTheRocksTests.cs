using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
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
            var files = Directory.GetFiles(path);
            foreach (var file in files)
            {
                File.Delete(file);
            }
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

        [Test]
        public void ReadWriteBatchTest()
        {
            var key = Bytes.FromHexString("0xf394a2bf7f206d49902aa06a30bbc12228622ba5c10e171b77e59303aded1b8fb2a194bc078e504ce68a202252987704c0c489a1e290f4c197c7a539cf2b1b63");
            var value = Bytes.FromHexString("0xf85eb840f394a2bf7f206d49902aa06a30bbc12228622ba5c10e171b77e59303aded1b8fb2a194bc078e504ce68a202252987704c0c489a1e290f4c197c7a539cf2b1b63943a3a666666663a3139322e3136382e312e31353482b660808201f4");
            var key2 = Bytes.FromHexString("0x9517c574ecba949586503215b78b57e0c5d4e686bbecd88a19e39060c743857693b56809037e7d580e9a6b3bfbaac45b7c8193815f36384a570d687719d61509");
            var value2 = Bytes.FromHexString("0xf85eb8409517c574ecba949586503215b78b57e0c5d4e686bbecd88a19e39060c743857693b56809037e7d580e9a6b3bfbaac45b7c8193815f36384a570d687719d61509943a3a666666663a3139322e3136382e312e31353482b5af808206d6");
            var key3 = Bytes.FromHexString("0xb3358e59df146ce58469a171afa2164cf8ccec6e7b89b4f32d4bf9125ddb8424f48496f1a7850626e0902ad0fede8d53d20b868cb0445705fc57f3c523a8e54e");
            var value3 = Bytes.FromHexString("0xf85eb840b3358e59df146ce58469a171afa2164cf8ccec6e7b89b4f32d4bf9125ddb8424f48496f1a7850626e0902ad0fede8d53d20b868cb0445705fc57f3c523a8e54e943a3a666666663a3139322e3136382e312e313534827667808207d1");
            var key4 = Bytes.FromHexString("0xf394a2bf7f206d49902aa06a30bbc12228622ba5c10e171b77e59303aded1b8fb2a194bc078e504ce68a202252987704c0c489a1e290f4c197c7a539cf2b1b63");
            var value4 = Bytes.FromHexString("0xf85eb840f394a2bf7f206d49902aa06a30bbc12228622ba5c10e171b77e59303aded1b8fb2a194bc078e504ce68a202252987704c0c489a1e290f4c197c7a539cf2b1b63943a3a666666663a3139322e3136382e312e31353482b660808207d0");

            _db.StartBatch();
            _db[key] = value;
            _db[key2] = value2;
            _db[key3] = value3;
            _db[key4] = value4;
            _db.CommitBatch();

            _db.StartBatch();
            _db[key] = value;
            _db[key2] = value;
            _db[key2] = value2;
            _db.CommitBatch();
        }
    }
}