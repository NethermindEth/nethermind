// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip2930;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class AccessListDecoderTests
    {
        private readonly AccessListDecoder _decoder = new();

        public static IEnumerable<(string, AccessList?)> TestCaseSource()
        {
            yield return (
                "null",
                null);

            // yield return ("empty", AccessList.Empty()); <-- null and empty are equivalent here

            yield return (
                "no storage",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .Build());

            yield return (
                "no storage 2",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddAddress(TestItem.AddressB)
                    .Build());

            yield return (
                "1-1",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddStorage(1)
                    .Build());

            yield return (
                "1-2",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddStorage(1)
                    .AddStorage(2)
                    .Build());

            yield return (
                "2-1",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddStorage(1)
                    .AddAddress(TestItem.AddressB)
                    .AddStorage(2)
                    .Build());

            yield return (
                "2-2",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddStorage(1)
                    .AddStorage(2)
                    .AddAddress(TestItem.AddressB)
                    .AddStorage(1)
                    .AddStorage(2)
                    .Build());

            yield return (
                "with duplicates",
                new AccessList.Builder()
                    .AddAddress(TestItem.AddressA)
                    .AddStorage(1)
                    .AddStorage(1)
                    .AddAddress(TestItem.AddressB)
                    .AddStorage(2)
                    .AddAddress(TestItem.AddressB)
                    .AddStorage(2)
                    .Build());
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip((string TestName, AccessList? AccessList) testCase)
        {
            byte[] bytes = new byte[_decoder.GetLength(testCase.AccessList, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _decoder.Encode(ref writer, testCase.AccessList);
            RlpReader ctx = new(bytes);
            AccessList decoded = _decoder.Decode(ref ctx)!;
            if (testCase.AccessList is null)
            {
                Assert.That(decoded, Is.Null);
            }
            else
            {
                Assert.That(decoded, Is.EqualTo(testCase.AccessList), testCase.TestName);
            }
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip_value((string TestName, AccessList? AccessList) testCase)
        {
            byte[] bytes = new byte[_decoder.GetLength(testCase.AccessList, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _decoder.Encode(ref writer, testCase.AccessList);
            RlpReader ctx = new(bytes.AsSpan());
            AccessList decoded = _decoder.Decode(ref ctx)!;
            if (testCase.AccessList is null)
            {
                Assert.That(decoded, Is.Null);
            }
            else
            {
                Assert.That(decoded, Is.EqualTo(testCase.AccessList), testCase.TestName);
            }
        }

        [Test]
        public void Get_length_returns_1_for_null() => Assert.That(_decoder.GetLength((AccessList?)null, RlpBehaviors.None), Is.EqualTo(1));

        [Test]
        public void Rejects_entry_missing_storage_keys_array()
        {
            const string error = "storage keys";
            byte[] invalid = Convert.FromHexString("d6d5940000000000000000000000000000000000000000");

            void DecodeStream()
            {
                RlpReader ctx = new(invalid.AsSpan());
                _decoder.Decode(ref ctx);
            }

            Assert.That(DecodeStream, Throws.InstanceOf<RlpException>().With.Message.Contain(error));

            void DecodeContext()
            {
                RlpReader ctx = new(invalid.AsSpan());
                _decoder.Decode(ref ctx);
            }

            Assert.That(DecodeContext, Throws.InstanceOf<RlpException>().With.Message.Contain(error));
        }
    }
}
