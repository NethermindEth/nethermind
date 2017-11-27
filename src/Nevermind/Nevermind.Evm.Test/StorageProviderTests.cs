using System;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;
using Nevermind.Store;
using NUnit.Framework;

namespace Nevermind.Evm.Test
{
    [TestFixture]
    public class StorageProviderTests
    {
        private readonly Address _address1 = new Address(Keccak.Compute("1"));
        private readonly Address _address2 = new Address(Keccak.Compute("2"));

        private readonly IStateProvider _stateProvider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);

        [SetUp]
        public void Setup()
        {
            _stateProvider.CreateAccount(_address1, 0);
            _stateProvider.CreateAccount(_address2, 0);
        }

        private readonly byte[][] _values =
        {
            new byte[] {0},
            new byte[] {1},
            new byte[] {2},
            new byte[] {3},
            new byte[] {4},
            new byte[] {5},
            new byte[] {6},
            new byte[] {7},
            new byte[] {8},
            new byte[] {9},
            new byte[] {10},
            new byte[] {11},
            new byte[] {12},
        };

        [Test]
        public void Empty_commit_restore()
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Commit();
            provider.Restore(-1);
        }

        private StorageProvider BuildStorageProvider()
        {
            ILogger stateLogger = ShouldLog.State ? new ConsoleLogger() : null;
            StorageProvider provider = new StorageProvider(new MultiDb(stateLogger), _stateProvider, stateLogger);
            return provider;
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_same_index_different_values_restore(int snapshot)
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 1, _values[2]);
            provider.Set(_address1, 1, _values[3]);
            provider.Restore(snapshot);

            Assert.AreEqual(_values[snapshot + 1], provider.Get(_address1, 1));
        }

        [Test]
        public void Keep_in_cache()
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Set(_address1, 1, _values[1]);
            provider.Commit();
            provider.Get(_address1, 1);
            provider.Set(_address1, 1, _values[2]);
            provider.Restore(-1);
            provider.Set(_address1, 1, _values[2]);
            provider.Restore(-1);
            provider.Set(_address1, 1, _values[2]);
            provider.Restore(-1);
            Assert.AreEqual(_values[1], provider.Get(_address1, 1));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_different_index(int snapshot)
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 2, _values[2]);
            provider.Set(_address1, 3, _values[3]);
            provider.Restore(snapshot);

            Assert.AreEqual(_values[Math.Min(snapshot + 1, 1)], provider.Get(_address1, 1));
        }

        [Test]
        public void Commit_restore()
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 2, _values[2]);
            provider.Set(_address1, 3, _values[3]);
            provider.Commit();
            provider.Set(_address2, 1, _values[4]);
            provider.Set(_address2, 2, _values[5]);
            provider.Set(_address2, 3, _values[6]);
            provider.Commit();
            provider.Set(_address1, 1, _values[7]);
            provider.Set(_address1, 2, _values[8]);
            provider.Set(_address1, 3, _values[9]);
            provider.Commit();
            provider.Set(_address2, 1, _values[10]);
            provider.Set(_address2, 2, _values[11]);
            provider.Set(_address2, 3, _values[12]);
            provider.Commit();
            provider.Restore(-1);

            Assert.AreEqual(_values[7], provider.Get(_address1, 1));
            Assert.AreEqual(_values[8], provider.Get(_address1, 2));
            Assert.AreEqual(_values[9], provider.Get(_address1, 3));
            Assert.AreEqual(_values[10], provider.Get(_address2, 1));
            Assert.AreEqual(_values[11], provider.Get(_address2, 2));
            Assert.AreEqual(_values[12], provider.Get(_address2, 3));
        }

        [Test]
        public void Commit_no_changes()
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 2, _values[2]);
            provider.Set(_address1, 3, _values[3]);
            provider.Restore(-1);
            provider.Commit();

            Assert.AreEqual(Keccak.EmptyTreeHash, provider.GetRoot(_address1));
        }

        [Test]
        public void Commit_no_changes_2()
        {
            StorageProvider provider = BuildStorageProvider();
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 1, _values[2]);
            provider.Set(_address1, 1, _values[3]);
            provider.Restore(2);
            provider.Restore(1);
            provider.Restore(0);
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Set(_address1, 1, _values[1]);
            provider.Set(_address1, 1, _values[2]);
            provider.Set(_address1, 1, _values[3]);
            provider.Restore(-1);
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Get(_address1, 1);
            provider.Commit();

            Assert.AreEqual(Keccak.EmptyTreeHash, provider.GetRoot(_address1));
        }
    }
}