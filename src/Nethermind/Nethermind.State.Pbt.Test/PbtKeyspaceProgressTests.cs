// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.State.Pbt.Image;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtKeyspaceProgressTests
{
    [Test]
    public void Walked_sums_completed_and_published_partitions()
    {
        PbtKeyspaceProgress progress = new(4);
        progress.Complete(0);
        progress.Publish(1, Bytes.FromHexString("0x6000000000000000"));
        Assert.That(progress.Walked, Is.EqualTo(PbtKeyspaceProgress.Keyspace / 8 * 3));

        for (int partition = 1; partition < 4; partition++) progress.Complete(partition);
        Assert.That(progress.Walked, Is.EqualTo(PbtKeyspaceProgress.Keyspace));
    }
}
