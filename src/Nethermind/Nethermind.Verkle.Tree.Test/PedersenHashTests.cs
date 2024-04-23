using System;
using FluentAssertions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;

namespace Nethermind.Verkle.Tree.Test
{
    [TestFixture]
    public class PedersenHashTests
    {
        private readonly byte[] _testAddressBytesZero;
        public PedersenHashTests()
        {
            _testAddressBytesZero = new byte[20];
        }

        [Test]
        public void PedersenHashTreeKey0()
        {
            byte[] hash = PedersenHash.Hash(_testAddressBytesZero, UInt256.Zero);
            hash[31] = 0;
            Convert.ToHexString(hash).Should().BeEquivalentTo("1A100684FD68185060405F3F160E4BB6E034194336B547BDAE323F888D533200");
        }

        [Test]
        public void PedersenHashTreeKey1()
        {
            Span<byte> address32 = Convert.FromHexString("71562b71999873DB5b286dF957af199Ec94617f7");
            byte[] hash = PedersenHash.Hash(address32, UInt256.Zero);
            hash[31] = 0;
            Convert.ToHexString(hash).Should().BeEquivalentTo("1540DFAD7755B40BE0768C6AA0A5096FBF0215E0E8CF354DD928A17834646600");
        }

    }
}
