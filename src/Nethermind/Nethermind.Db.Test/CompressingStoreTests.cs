// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
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

            ctx.Compressed[Key] = null;

            Assert.IsNull(ctx.Compressed[Key]);
            Assert.IsNull(ctx.Wrapped[Key]);
        }

        [Test]
        public void Empty()
        {
            Context ctx = new();

            byte[] empty = Array.Empty<byte>();

            ctx.Compressed[Key] = empty;

            CollectionAssert.AreEqual(empty, ctx.Compressed[Key]);
            CollectionAssert.AreEqual(empty, ctx.Wrapped[Key]);
        }

        [Test]
        public void Single()
        {
            Context ctx = new();

            byte[] value = { 13 };

            ctx.Compressed[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressed[Key]);
            CollectionAssert.AreEqual(value, ctx.Wrapped[Key]);
        }

        [Test]
        public void EOA()
        {
            Context ctx = new();

            Rlp encoded = new AccountDecoder().Encode((Account)new(1));
            ctx.Compressed[Key] = encoded.Bytes;

            CollectionAssert.AreEqual(encoded.Bytes, ctx.Compressed[Key]);
            ctx.Wrapped[Key]!.Length.Should().Be(5);
        }

        [Test]
        public void Backward_compatible_read()
        {
            Context ctx = new();
            byte[] value = { 1, 2, 34 };

            ctx.Wrapped[Key] = value;

            CollectionAssert.AreEqual(value, ctx.Compressed[Key]);
        }

        [Test]
        public void Batch()
        {
            Context ctx = new();

            using (IBatch batch = ctx.Compressed.StartBatch())
            {
                batch[Key] = EOABytes;
            }

            CollectionAssert.AreEqual(EOABytes, ctx.Compressed[Key]);

            ctx.Wrapped[Key]!.Length.Should().Be(5);
        }

        private class Context
        {
            public TestMemDb Wrapped { get; }

            public IDb Compressed { get; }

            public Context()
            {
                Wrapped = new TestMemDb();
                Compressed = Wrapped.WithEOACompressed();
            }
        }

        private static readonly byte[] EOABytes = new AccountDecoder().Encode((Account)new(1)).Bytes;

        private static readonly byte[] Key = { 1 };
    }
}
