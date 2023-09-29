// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class AddressTests
    {
        [TestCase("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        [TestCase("0x5a4eab120fb44eb6684e5e32785702ff45ea344d", "0x5a4eab120fb44eb6684e5e32785702ff45ea344d")]
        public void String_representation_is_correct(string init, string expected)
        {
            Address address = new(init);
            string addressString = address.ToString();
            Assert.That(addressString, Is.EqualTo(expected));
        }

        [TestCase("0x52908400098527886E0F7030069857D2E4169EE7", "0x52908400098527886E0F7030069857D2E4169EE7")]
        [TestCase("0x8617E340B3D01FA5F11F306F4090FD50E238070D", "0x8617E340B3D01FA5F11F306F4090FD50E238070D")]
        [TestCase("0xde709f2102306220921060314715629080e2fb77", "0xde709f2102306220921060314715629080e2fb77")]
        [TestCase("0x27b1fdb04752bbc536007a920d24acb045561c26", "0x27b1fdb04752bbc536007a920d24acb045561c26")]
        [TestCase("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
        [TestCase("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359", "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
        [TestCase("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB", "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
        [TestCase("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb", "0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
        [TestCase("0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741", "0x5be4BDC48CeF65dbCbCaD5218B1A7D37F58A0741")]
        [TestCase("0x5A4EAB120fB44eb6684E5e32785702FF45ea344D", "0x5A4EAB120fB44eb6684E5e32785702FF45ea344D")]
        [TestCase("0xa7dD84573f5ffF821baf2205745f768F8edCDD58", "0xa7dD84573f5ffF821baf2205745f768F8edCDD58")]
        [TestCase("0x027a49d11d118c0060746F1990273FcB8c2fC196", "0x027a49d11d118c0060746F1990273FcB8c2fC196")]
        public void String_representation_with_checksum_is_correct(string init, string expected)
        {
            Address address = new(init);
            string addressString = address.ToString(true);
            Assert.That(addressString, Is.EqualTo(expected));
        }

        [TestCase("0x52908400098527886E0F7030069857D2E4169EE7", true, true)]
        [TestCase("52908400098527886E0F7030069857D2E4169EE7", true, true)]
        [TestCase("0x52908400098527886E0F7030069857D2E4169EE7", false, false)]
        [TestCase("52908400098527886E0F7030069857D2E4169EE7", false, true)]
        public void Can_check_if_address_is_valid(string addressHex, bool allowPrefix, bool expectedResult)
        {
            Assert.That(Address.IsValidAddress(addressHex, allowPrefix), Is.EqualTo(expectedResult));
        }

        [Test]
        public void Bytes_are_correctly_assigned()
        {
            byte[] bytes = new byte[20];
            new System.Random(1).NextBytes(bytes);
            Address address = new(bytes);
            Assert.True(Bytes.AreEqual(address.Bytes, bytes));
        }

        [Test]
        public void Equals_works()
        {
            Address addressA = new(Keccak.Compute("a"));
            Address addressA2 = new(Keccak.Compute("a"));
            Address addressB = new(Keccak.Compute("b"));
            Assert.True(addressA.Equals(addressA2));
            // ReSharper disable once EqualExpressionComparison
            Assert.True(addressA.Equals(addressA));
            Assert.False(addressA.Equals(addressB));
            Assert.False(addressA.Equals(null));
        }

        [Test]
        public void Equals_operator_works()
        {
            Address addressA = new(Keccak.Compute("a"));
            Address addressA2 = new(Keccak.Compute("a"));
            Address addressB = new(Keccak.Compute("b"));
            Assert.True(addressA == addressA2);
            // ReSharper disable once EqualExpressionComparison
#pragma warning disable CS1718
            Assert.True(addressA == addressA);
#pragma warning restore CS1718
            Assert.False(addressA == addressB);
            Assert.False(addressA is null);
            Assert.False(null == addressA);
            {
                Address? address = null;
                Assert.True(address is null);
            }
        }

        [Test]
        public void Not_equals_operator_works()
        {
            Address addressA = new(Keccak.Compute("a"));
            Address addressA2 = new(Keccak.Compute("a"));
            Address addressB = new(Keccak.Compute("b"));
            Assert.False(addressA != addressA2);
            // ReSharper disable once EqualExpressionComparison
#pragma warning disable CS1718
            Assert.False(addressA != addressA);
#pragma warning restore CS1718
            Assert.True(addressA != addressB);
            Assert.True(addressA is not null);
            Assert.True(null != addressA);
            {
                Address? address = null;
                Assert.False(address is not null);
            }
        }

        [Test]
        public void Is_precompiled_1()
        {
            byte[] addressBytes = new byte[20];
            addressBytes[19] = 1;
            Address address = new(addressBytes);
            Assert.True(address.IsPrecompile(Frontier.Instance));
        }

        [Test]
        public void Is_precompiled_4_regression()
        {
            byte[] addressBytes = new byte[20];
            addressBytes[19] = 4;
            Address address = new(addressBytes);
            Assert.True(address.IsPrecompile(Frontier.Instance));
        }

        [Test]
        public void Is_precompiled_5_frontier()
        {
            byte[] addressBytes = new byte[20];
            addressBytes[19] = 5;
            Address address = new(addressBytes);
            Assert.False(address.IsPrecompile(Frontier.Instance));
        }

        [Test]
        public void Is_precompiled_5_byzantium()
        {
            byte[] addressBytes = new byte[20];
            addressBytes[19] = 5;
            Address address = new(addressBytes);
            Assert.True(address.IsPrecompile(Byzantium.Instance));
        }

        [Test]
        public void Is_precompiled_9_byzantium()
        {
            byte[] addressBytes = new byte[20];
            addressBytes[19] = 9;
            Address address = new(addressBytes);
            Assert.False(address.IsPrecompile(Byzantium.Instance));
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(1000, false)]
        public void From_number_for_precompile(int number, bool isPrecompile)
        {
            Address address = Address.FromNumber((UInt256)number);
            Assert.That(address.IsPrecompile(Byzantium.Instance), Is.EqualTo(isPrecompile));
        }

        [TestCase(0, "0x24cd2edba056b7c654a50e8201b619d4f624fdda")]
        [TestCase(1, "0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8")]
        public void Of_contract(long nonce, string expectedAddress)
        {
            Address address = ContractAddress.From(TestItem.AddressA, (UInt256)nonce);
            Assert.That(new Address(expectedAddress), Is.EqualTo(address));
        }

        [TestCaseSource(nameof(PointEvaluationPrecompileTestCases))]
        public bool Is_PointEvaluationPrecompile_properly_activated(IReleaseSpec spec) =>
            Address.FromNumber(0x0a).IsPrecompile(spec);

        public static IEnumerable PointEvaluationPrecompileTestCases
        {
            get
            {
                yield return new TestCaseData(Shanghai.Instance) { ExpectedResult = false, TestName = nameof(Shanghai) };
                yield return new TestCaseData(Cancun.Instance) { ExpectedResult = true, TestName = nameof(Cancun) };
            }
        }

        [Test]
        public void There_are_no_duplicates_in_known_addresses()
        {
            _ = KnownAddresses.GoerliValidators;
            _ = KnownAddresses.KnownMiners;
            _ = KnownAddresses.RinkebyValidators;
        }
    }
}
