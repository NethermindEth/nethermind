// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [Parallelizable(ParallelScope.All)]
    public class CompressingStoreTests
    {
        [Test]
        public void Null()
        {
            Context ctx = new();

            ctx.Compressing[Key] = null;

            Assert.IsNull(ctx.Compressing[Key]);
            Assert.IsNull(ctx.Wrapped[Key]);
        }

        [Test]
        public void Empty()
        {
            Context ctx = new();

            byte[] empty = Array.Empty<byte>();

            ctx.Compressing[Key] = empty;

            CollectionAssert.AreEqual(empty, ctx.Compressing[Key]);
            CollectionAssert.AreEqual(empty, ctx.Wrapped[Key]);
        }

        [Test]
        public void Single()
        {
            Context ctx = new();

            byte[] value = { 13 };

            ctx.Compressing[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressing[Key]);
            CollectionAssert.AreEqual(value, ctx.Wrapped[Key]);
        }

        [TestCaseSource(nameof(BuildCases))]
        public void Run(byte[] value, int expectedRawLength)
        {
            Context ctx = new();

            ctx.Compressing[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressing[Key]);
            ctx.Wrapped[Key]!.Length.Should().Be(expectedRawLength);
        }

        public static IEnumerable<TestCaseData> BuildCases()
        {
            byte[] emptyTree = Rlp.Encode(Keccak.EmptyTreeHash.Bytes).Bytes;
            byte[] emptyString = Rlp.Encode(Keccak.OfAnEmptyString.Bytes).Bytes;

            yield return new TestCaseData(emptyTree, 3)
                .SetName("EmptyTreeBytes_only");

            byte[] emptyTreeMiddle = Pre.Concat(emptyTree).Concat(Post).ToArray();
            yield return new TestCaseData(emptyTreeMiddle, emptyTreeMiddle.Length - 33 + 3)
                .SetName("EmptyTreeBytes_in_the_middle");

            yield return new TestCaseData(emptyString, 3)
                .SetName("EmptyStringBytes_only");

            byte[] emptyStringMiddle = Pre.Concat(emptyString).Concat(Post).ToArray();
            yield return new TestCaseData(emptyStringMiddle, emptyStringMiddle.Length - 33 + 3)
                .SetName("EmptyStringBytes_in_the_middle");

            byte[] emptyStringThenEmptyTree = Pre.Concat(emptyString).Concat(Middle).Concat(emptyTree).Concat(Post).ToArray();
            yield return new TestCaseData(emptyStringThenEmptyTree, emptyStringThenEmptyTree.Length - 66 + 3)
                .SetName("EmptyString_then_EmptyTree");

            byte[] emptyTreeThenEmptyString = Pre.Concat(emptyTree).Concat(Middle).Concat(emptyString).Concat(Post).ToArray();
            yield return new TestCaseData(emptyTreeThenEmptyString, emptyTreeThenEmptyString.Length - 66 + 3)
                .SetName("EmptyTree_then_EmptyString");
        }

        private class Context
        {
            public TestMemDb Wrapped { get; }

            public CompressingStore Compressing { get; }

            public Context()
            {
                Wrapped = new TestMemDb();
                Compressing = new CompressingStore(Wrapped);
            }
        }

        private static readonly byte[] Key = { 1 };

        private static readonly byte[] Pre = { 2, 43, 46, 42 };
        private static readonly byte[] Middle = { 3, 64, 3, 4, 6, 3, 4, 5, 4 };
        private static readonly byte[] Post = { 53, 63, 234, 56, 4, 23 };
    }
}
