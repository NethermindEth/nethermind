using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Db.Test
{

    public class LogIndexTests
    {

        [Test]
        public void Can_get_all_on_empty()
        {
            IDb logIndexDb = new MemDb();
            Address x = Address.Zero;

            Span<byte> key = stackalloc byte[24];
            x.Bytes.CopyTo(key);
            Span<byte> last4bytes = key[20..];
            BinaryPrimitives.WriteInt32BigEndian(last4bytes, 10_000_00);
            byte[] value = [1, 2, 3, 4, 5];
            logIndexDb.PutSpan(key, value);

            var result = logIndexDb.GetAll();
            result.Should().NotBeEmpty();
        }
    }
}
