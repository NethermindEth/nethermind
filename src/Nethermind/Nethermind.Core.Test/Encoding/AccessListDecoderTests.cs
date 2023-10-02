// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
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
            RlpStream rlpStream = new(10000);
            _decoder.Encode(rlpStream, testCase.AccessList);
            rlpStream.Position = 0;
            AccessList decoded = _decoder.Decode(rlpStream)!;
            if (testCase.AccessList is null)
            {
                decoded.Should().BeNull();
            }
            else
            {
                decoded.AsEnumerable().Should().BeEquivalentTo(testCase.AccessList.AsEnumerable(), testCase.TestName);
            }
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip_value((string TestName, AccessList? AccessList) testCase)
        {
            RlpStream rlpStream = new(10000);
            _decoder.Encode(rlpStream, testCase.AccessList);
            rlpStream.Position = 0;
            Rlp.ValueDecoderContext ctx = rlpStream.Data.AsRlpValueContext();
            AccessList decoded = _decoder.Decode(ref ctx)!;
            if (testCase.AccessList is null)
            {
                decoded.Should().BeNull();
            }
            else
            {
                decoded.AsEnumerable().Should().BeEquivalentTo(testCase.AccessList.AsEnumerable(), testCase.TestName);
            }
        }

        [Test]
        public void Get_length_returns_1_for_null()
        {
            _decoder.GetLength(null, RlpBehaviors.None).Should().Be(1);
        }
    }
}
