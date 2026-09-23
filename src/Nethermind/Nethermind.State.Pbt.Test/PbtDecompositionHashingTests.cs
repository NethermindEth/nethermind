// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Pins what an update hashes, which decomposition must never inflate.</summary>
public class PbtDecompositionHashingTests
{
    private static byte[] Value(byte seed)
    {
        byte[] value = new byte[32];
        value[31] = seed;
        return value;
    }

    /// <summary>A dense group leaves its interior implicit, so a touched path is resolved without rebuilding one.</summary>
    /// <remarks>
    /// Sixteen keys differing in the first nibble fill the root group's boundary slots. Rewriting values hashes the new
    /// leaves and the nodes composed above them, plus each touched boundary encoding the group stores no link for.
    /// Nothing else is hashed: resolving a boundary node reads its hash from the link that names it, and never rebuilds
    /// the implicit branches above it only to discard them, which cost a further 2, 2 and 12 hashes here.
    /// </remarks>
    [Test]
    public void Dense_group_update_hashes_nothing_it_does_not_rebuild([Values(1, 2, 16)] int touched)
    {
        using PbtTreeHarness tree = new();
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[16];
        for (int slot = 0; slot < entries.Length; slot++) entries[slot] = (Bytes.FromHexString($"{slot << 4:X2}00"), Value((byte)(slot + 1)));
        tree.ApplyBatch(entries);
        tree.Reopen();

        TrieUpdaterMetrics metrics = new();
        (byte[] Key, byte[]? Value)[] rewrites = new (byte[], byte[]?)[touched];
        for (int index = 0; index < touched; index++) rewrites[index] = (entries[index].Key, Value(0x40));
        tree.ApplyBatch(rewrites, metrics);

        TestContext.Out.WriteLine($"touched {touched}: node hashes {metrics.NodeHashes}");
        Assert.That(metrics.NodeHashes, Is.EqualTo(touched switch { 1 => 15, 2 => 16, _ => 31 }));
    }
}
