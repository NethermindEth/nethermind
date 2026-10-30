// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class LengthListTests
{
    [Test]
    public void Reads_lengths_in_the_order_containers_were_begun([Values(0, 2, 64)] int initialLength)
    {
        LengthList lengths = new(new int[initialLength]);
        try
        {
            LengthListWriter root = lengths.BeginRootContainer();
            using (LengthListWriter outer = root.BeginContainer())
            {
                using (LengthListWriter inner = outer.BeginContainer())
                {
                    inner.Add(60);
                }

                outer.Add(3);
            }

            using (LengthListWriter last = root.BeginContainer())
            {
                last.Add(1);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Length, Is.EqualTo(Rlp.LengthOfSequence(3 + Rlp.LengthOfSequence(60)) + Rlp.LengthOfSequence(1)));
                Assert.That(new[] { lengths.Read(), lengths.Read(), lengths.Read() }, Is.EqualTo(new[] { 3 + Rlp.LengthOfSequence(60), 60, 1 }));
                Assert.That(lengths.UnreadCount, Is.Zero);
            }

            LengthListWriter reused = lengths.BeginRootContainer();
            reused.Add(5);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reused.Length, Is.EqualTo(5));
                Assert.That(lengths.UnreadCount, Is.Zero);
            }
        }
        finally
        {
            lengths.Dispose();
        }
    }
}
