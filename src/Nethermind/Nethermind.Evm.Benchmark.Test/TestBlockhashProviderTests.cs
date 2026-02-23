// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class TestBlockhashProviderTests
{
    [Test]
    public void GetBlockhash_Returns_NonNull_Hash()
    {
        TestBlockhashProvider provider = new();
        Nethermind.Core.BlockHeader header = Build.A.BlockHeader.TestObject;

        Hash256 hash = provider.GetBlockhash(header, 42, Prague.Instance);

        Assert.That(hash, Is.Not.Null);
    }

    [Test]
    public void GetBlockhash_Returns_Deterministic_Hash_For_Same_Number()
    {
        TestBlockhashProvider provider = new();
        Nethermind.Core.BlockHeader header = Build.A.BlockHeader.TestObject;

        Hash256 hash1 = provider.GetBlockhash(header, 42, Prague.Instance);
        Hash256 hash2 = provider.GetBlockhash(header, 42, Prague.Instance);

        Assert.That(hash1, Is.EqualTo(hash2));
    }

    [Test]
    public void GetBlockhash_Returns_Different_Hash_For_Different_Numbers()
    {
        TestBlockhashProvider provider = new();
        Nethermind.Core.BlockHeader header = Build.A.BlockHeader.TestObject;

        Hash256 hash1 = provider.GetBlockhash(header, 1, Prague.Instance);
        Hash256 hash2 = provider.GetBlockhash(header, 2, Prague.Instance);

        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }

    [Test]
    public void Prefetch_Returns_Completed_Task()
    {
        TestBlockhashProvider provider = new();
        Nethermind.Core.BlockHeader header = Build.A.BlockHeader.TestObject;

        System.Threading.Tasks.Task task = provider.Prefetch(header, default);

        Assert.That(task.IsCompleted, Is.True);
    }
}
