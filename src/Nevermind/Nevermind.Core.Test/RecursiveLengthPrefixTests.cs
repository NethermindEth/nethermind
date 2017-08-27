using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class RecursiveLengthPrefixTests
    {
        [TestMethod]
        public void Serialized_form_is_same_as_input_when_input_length_is_1_and_value_is_less_than_128()
        {
            Assert.AreEqual(0, RecursiveLengthPrefix.Serialize(new byte[] {0})[0], "0");
            Assert.AreEqual(127, RecursiveLengthPrefix.Serialize(new byte[] {127})[0], "128");
            Assert.AreEqual(1, RecursiveLengthPrefix.Serialize(new byte[] {1})[0], "1");
        }

        [TestMethod]
        public void Serialized_form_is_128_when_input_is_empty()
        {
            Assert.AreEqual(128, RecursiveLengthPrefix.Serialize(new byte[] { })[0]);
        }

        [TestMethod]
        public void
            Serialized_form_is_input_prefixed_with_128_plus_length_of_input_when_input_length_is_between_1_and_56()
        {
            byte[] input = new byte[55];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            Assert.AreEqual(183, RecursiveLengthPrefix.Serialize(input)[0]);
            Assert.AreEqual(56, RecursiveLengthPrefix.Serialize(input).Length);
            for (int i = 0; i < 55; i++)
            {
                Assert.AreEqual(input[i], RecursiveLengthPrefix.Serialize(input)[i + 1]);
            }
        }

        [DataTestMethod]
        [DataRow(128L, (byte) (1 + 183), (byte) 128)]
        [DataRow(256L, (byte) (1 + 1 + 183), (byte) 1)]
        [DataRow(256L * 256L, (byte) (1 + 2 + 183), (byte) 1)]
        [DataRow(256L * 256L * 256L, (byte) (1 + 3 + 183), (byte) 1)]
        //[DataRow(256L * 256L * 256L * 256L, (byte)(1 + 4 + 183), (byte)1)]
        public void Serialized_form_is_input_prefixed_with_big_endian_length_and_prefixed_with_length_of_it_plus_183(
            long inputLength, byte expectedFirstByte, byte expectedSecondByte)
        {
            byte[] input = new byte[inputLength];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            Assert.AreEqual(expectedFirstByte, RecursiveLengthPrefix.Serialize(input)[0]);
            Assert.AreEqual(expectedSecondByte, RecursiveLengthPrefix.Serialize(input)[1]);

            if (inputLength < 256)
            {
                for (int i = 0; i < 128; i++)
                {
                    Assert.AreEqual(input[i], RecursiveLengthPrefix.Serialize(input)[i + 1 + expectedFirstByte - 183]);
                }
            }
        }

        [TestMethod] // not yet sure how it works
        public void Serializing_sequences()
        {
            byte[] output = RecursiveLengthPrefix.Serialize(255L, new byte[] { 255 });
            Assert.AreEqual(4, output.Length);
        }

        [TestMethod] // not yet sure how it works
        public void Serializing_empty_sequence()
        {
            byte[] output = RecursiveLengthPrefix.Serialize();
            Assert.AreEqual(1, output.Length);
            Assert.AreEqual(128, output[0]);
        }
    }
}
