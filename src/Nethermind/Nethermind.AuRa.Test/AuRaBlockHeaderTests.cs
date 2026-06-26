// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class AuRaBlockHeaderTests
{
    /// <summary>Guards the hand-maintained member roster in <see cref="AuRaBlockHeader.UpgradeFrom"/>.</summary>
    [Test]
    public void UpgradeFrom_copies_every_settable_BlockHeader_member()
    {
        BlockHeader src = new(
            Keccak.Compute("parent"), Keccak.Compute("uncles"), Address.Zero, 1, 2, 3, 4, [5]);
        BlockHeaderMembers.FillWithDistinctValues(src);

        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(src);

        using (Assert.EnterMultipleScope())
        {
            foreach (PropertyInfo property in BlockHeaderMembers.SettableProperties)
            {
                Assert.That(property.GetValue(upgraded), Is.EqualTo(property.GetValue(src)), property.Name);
            }

            foreach (FieldInfo field in BlockHeaderMembers.PublicFields)
            {
                Assert.That(field.GetValue(upgraded), Is.EqualTo(field.GetValue(src)), field.Name);
            }
        }
    }
}
