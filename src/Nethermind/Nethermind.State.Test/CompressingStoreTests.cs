// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
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

        [Test]
        public void Backward_compatible_read()
        {
            Context ctx = new();
            byte[] value = { 1, 2, 34 };

            ctx.Wrapped[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressing[Key]);
        }

        [Test]
        public void Compression_is_safe_for_long_buffers()
        {
            Context ctx = new();
            byte[] value = Enumerable.Range(1, 253).Select(i => (byte)i).Concat(EmptyString).ToArray();

            ctx.Compressing[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressing[Key]);
            ctx.Wrapped[Key]!.Length.Should().Be(value.Length);
        }

        [Test]
        public void Batch()
        {
            Context ctx = new();

            using (IBatch batch = ctx.Compressing.StartBatch())
            {
                batch[Key] = EmptyString;
            }

            CollectionAssert.AreEqual(EmptyString, ctx.Compressing[Key]);

            ctx.Wrapped[Key]!.Length.Should().Be(3);
        }

        public static IEnumerable<TestCaseData> BuildCases()
        {
            yield return new TestCaseData(EmptyTree, 3)
                .SetName("EmptyTreeBytes_only");

            byte[] emptyTreeMiddle = Pre.Concat(EmptyTree).Concat(Post).ToArray();
            yield return new TestCaseData(emptyTreeMiddle, emptyTreeMiddle.Length - 33 + 3)
                .SetName("EmptyTreeBytes_in_the_middle");

            yield return new TestCaseData(EmptyString, 3)
                .SetName("EmptyStringBytes_only");

            byte[] emptyStringMiddle = Pre.Concat(EmptyString).Concat(Post).ToArray();
            yield return new TestCaseData(emptyStringMiddle, emptyStringMiddle.Length - 33 + 3)
                .SetName("EmptyStringBytes_in_the_middle");

            byte[] emptyStringThenEmptyTree = Pre.Concat(EmptyString).Concat(Middle).Concat(EmptyTree).Concat(Post).ToArray();
            yield return new TestCaseData(emptyStringThenEmptyTree, emptyStringThenEmptyTree.Length - 66 + 3)
                .SetName("EmptyString_then_EmptyTree");

            byte[] emptyTreeThenEmptyString = Pre.Concat(EmptyTree).Concat(Middle).Concat(EmptyString).Concat(Post).ToArray();
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

        private static readonly byte[] EmptyTree = Rlp.Encode(Keccak.EmptyTreeHash.Bytes).Bytes;
        private static readonly byte[] EmptyString = Rlp.Encode(Keccak.OfAnEmptyString.Bytes).Bytes;

        private static readonly byte[] Key = { 1 };

        private static readonly byte[] Pre = { 2, 43, 46, 42 };
        private static readonly byte[] Middle = { 3, 64, 3, 4, 6, 3, 4, 5, 4 };
        private static readonly byte[] Post = { 53, 63, 234, 56, 4, 23 };
    }
}
