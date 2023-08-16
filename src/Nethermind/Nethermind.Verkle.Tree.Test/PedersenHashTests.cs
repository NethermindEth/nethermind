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
            Convert.ToHexString(hash).Should().BeEquivalentTo("bf101a6e1c8e83c11bd203a582c7981b91097ec55cbd344ce09005c1f26d1900");
        }

        [Test]
        public void PedersenHashTreeKey1()
        {
            Span<byte> address32 = Convert.FromHexString("71562b71999873DB5b286dF957af199Ec94617f7");
            byte[] hash = PedersenHash.Hash(address32, UInt256.Zero);
            hash[31] = 0;
            Convert.ToHexString(hash).Should().BeEquivalentTo("274cde18dd9dbb04caf16ad5ee969c19fe6ca764d5688b5e1d419f4ac6cd1600");
        }

    }
}
