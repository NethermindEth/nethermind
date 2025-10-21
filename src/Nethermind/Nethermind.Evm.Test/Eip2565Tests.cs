// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Numerics;
using FluentAssertions;
using MathNet.Numerics.Random;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2565Tests
    {
        const string Length64 = "0000000000000000000000000000000000000000000000000000000000000040";

        [Test]
        public void Simple_routine([Random(int.MinValue, int.MaxValue, 100)] int seed)
        {
            Random random = new(seed);
            byte[] data = random.NextBytes(3 * 64);
            string randomInput = string.Format("{0}{0}{0}{1}", Length64, data.ToHexString());

            Prepare input = Prepare.EvmCode.FromCode(randomInput);
            byte[] inputData = input.Done.ToArray();

            (ReadOnlyMemory<byte>, bool) gmpPair = ModExpPrecompile.Instance.Run(inputData, Berlin.Instance);
            (ReadOnlyMemory<byte>, bool) bigIntPair = BigIntegerModExp(inputData);

            Assert.That(gmpPair.Item1.ToArray(), Is.EqualTo(bigIntPair.Item1.ToArray()));
        }

        [Test]
        public void Overflow_gas_cost()
        {
            Prepare input = Prepare.EvmCode.FromCode("0000000000000000000000000000000000000000000000000000000000000001200000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000010001");
            long gas = ModExpPrecompile.Instance.DataGasCost(input.Done, Berlin.Instance).Data;
            gas.Should().Be(long.MaxValue);
        }

        [TestCase("0x00000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
        public void ModExp_run_should_not_throw_exception(string inputStr)
        {
            Prepare input = Prepare.EvmCode.FromCode(inputStr);
            Assert.DoesNotThrow(() => ModExpPrecompile.Instance.Run(input.Done.ToArray(), London.Instance));
            long gas = ModExpPrecompile.Instance.DataGasCost(input.Done, London.Instance).Data;
            gas.Should().Be(200);
        }

        private static (byte[], bool) BigIntegerModExp(byte[] inputData)
        {
            (int baseLength, int expLength, int modulusLength) = GetInputLengths(inputData);

            BigInteger modulusInt = inputData
                .SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

            if (modulusInt.IsZero)
            {
                return (new byte[modulusLength], true);
            }

            BigInteger baseInt = inputData.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
            BigInteger expInt = inputData.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength)
                .ToUnsignedBigInteger();
            return (BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength), true);
        }

        private static (int baseLength, int expLength, int modulusLength) GetInputLengths(ReadOnlySpan<byte> inputData)
        {
            Span<byte> extendedInput = stackalloc byte[96];
            inputData[..Math.Min(96, inputData.Length)]
                .CopyTo(extendedInput[..Math.Min(96, inputData.Length)]);

            int baseLength = (int)new UInt256(extendedInput[..32], true);
            UInt256 expLengthUint256 = new(extendedInput.Slice(32, 32), true);
            int expLength = expLengthUint256 > Array.MaxLength ? Array.MaxLength : (int)expLengthUint256;
            int modulusLength = (int)new UInt256(extendedInput.Slice(64, 32), true);

            return (baseLength, expLength, modulusLength);
        }
    }
}
