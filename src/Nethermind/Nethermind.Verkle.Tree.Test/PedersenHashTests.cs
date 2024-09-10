using System;
using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;

namespace Nethermind.Verkle.Tree.Test
{
    [TestFixture]
    public class PedersenHashTests
    {
        public static Random Random { get; } = new();
        private readonly byte[] _testAddressBytesZero;
        public PedersenHashTests()
        {
            _testAddressBytesZero = new byte[20];
        }

        [Test]
        public void BenchPedersenHash()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < 10; i++)
            {
                Random.NextBytes(key);
                PedersenHash.HashRust(key, UInt256.Zero);
            }

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                Random.NextBytes(key);
                byte[] hash = PedersenHash.HashRust(key, UInt256.Zero);
                // Console.WriteLine(hash.ToHexString());
            }
            Console.WriteLine($"Elapsed time: {sw.Elapsed} ms");
        }

        [Test]
        public void BenchPedersenHashV2()
        {
            byte[] key = new byte[20];
            for (int i = 0; i < 10; i++)
            {
                Random.NextBytes(key);
            }

            var xx = RustVerkleLib.VerkleContextNew();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                var data = UInt256.Zero.ToBigEndian();
                Random.NextBytes(key);
                byte[] hash = new byte[32];
                RustVerkleLib.VerklePedersenhash(xx, key, data, hash);
            }
            Console.WriteLine($"Elapsed time: {sw.Elapsed} ms");
        }

        [Test]
        public void PedersenHashTreeKey0()
        {
            byte[] hash = PedersenHash.HashRust(_testAddressBytesZero, UInt256.Zero);
            hash[31] = 0;
            Convert.ToHexString(hash).Should().BeEquivalentTo("1A100684FD68185060405F3F160E4BB6E034194336B547BDAE323F888D533200");
        }

        [Test]
        public void PedersenHashTreeKey1()
        {
            byte[] address32 = Convert.FromHexString("71562b71999873DB5b286dF957af199Ec94617f7");
            byte[] hash = PedersenHash.HashRust(address32, UInt256.Zero);
            hash[31] = 0;
            Convert.ToHexString(hash).Should().BeEquivalentTo("1540DFAD7755B40BE0768C6AA0A5096FBF0215E0E8CF354DD928A17834646600");
        }

    }
}
