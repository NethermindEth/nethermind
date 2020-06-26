using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography;
using Nethermind.Core2.Types;
using NUnit.Framework;

namespace Nethermind.Core.Cryptography.Test
{
    public class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T Get(string name)
        {
            return CurrentValue;
        }

        public IDisposable OnChange(Action<T, string> listener)
        {
            throw new NotImplementedException();
        }

        public T CurrentValue { get; }
    }
    
    public static class Default
    {
        public static IOptionsMonitor<T> Options<T>() where T : class, new()
        {
            return new TestOptionsMonitor<T>(new T());
        }
    }
    
    public class CryptographyServiceTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Test()
        {
            ICryptographyService service = new CryptographyService(
                new ChainConstants(),
                Default.Options<MiscellaneousParameters>(),
                Default.Options<TimeParameters>(),
                Default.Options<StateListLengths>(),
                Default.Options<MaxOperationsPerBlock>());
            
            List<Ref<DepositData>> depositDataOrRoots = new List<Ref<DepositData>>();
            depositDataOrRoots.Add(new Ref<DepositData>(new DepositData(BlsPublicKey.Zero, Bytes32.Zero, Gwei.One, BlsSignature.Zero)));
            depositDataOrRoots.Add(new Ref<DepositData>(new DepositData(BlsPublicKey.Zero, Bytes32.Zero, Gwei.One, BlsSignature.Zero)));
            Root a = service.HashTreeRoot(depositDataOrRoots);
            
            List<Ref<DepositData>> depositData = new List<Ref<DepositData>>();
            depositData.Add(new Ref<DepositData>(new DepositData(BlsPublicKey.Zero, Bytes32.Zero, Gwei.One, BlsSignature.Zero)));
            depositData.Add(new Ref<DepositData>(new DepositData(BlsPublicKey.Zero, Bytes32.Zero, Gwei.One, BlsSignature.Zero)));
            Root b = service.HashTreeRoot(depositData);
            
            Assert.AreEqual(a, b);
        }
    }
}