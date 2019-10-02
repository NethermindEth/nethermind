using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.SimpleSerialize.Tests
{
    [TestClass]
    public class SszNumberTest
    {
        [DataTestMethod]
        [DataRow((ulong)0, "0000000000000000")]
        [DataRow((ulong)0x0123456789abcdef, "efcdab8967452301")]
        public void UInt64Serialize(ulong value, string expectedByteString)
        {
            // Arrange
            var node = new SszNumber(value);

            // Act
            var bytes = node.ToBytes();

            // Assert
            var byteString = BitConverter.ToString(bytes.ToArray()).Replace("-", "").ToLowerInvariant();
            byteString.ShouldBe(expectedByteString);
        }
    }
}
