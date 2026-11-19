// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Arkiv.Execution;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

public class ArkivPurgeTests
{
    /// <summary>
    /// Every Rabdi block's purge, rebuilt from its number and keys alone, is that block's transaction to the byte — the
    /// same envelope key, fields and (RFC 6979) signature arkiv-reth's builder produced.
    /// </summary>
    [Test]
    public void Rebuilds_every_rabdi_purge_exactly()
    {
        int withKeys = 0;
        foreach (Block block in RabdiFixture.Blocks)
        {
            Transaction original = block.Transactions[0];
            Hash256[] keys = Enumerable.Range(0, (original.Data.Length - 68) / 32)
                .Select(i => new Hash256(original.Data.Span.Slice(68 + 32 * i, 32))).ToArray();
            withKeys += keys.Length > 0 ? 1 : 0;

            Transaction rebuilt = ArkivPurge.Create(RabdiFixture.ChainId, block.Number, keys);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(rebuilt.Hash, Is.EqualTo(original.Hash), $"block {block.Number}");
                Assert.That(rebuilt.SenderAddress, Is.EqualTo(original.SenderAddress));
            }
        }
        Assert.That(withKeys, Is.GreaterThanOrEqualTo(3));
    }
}
