// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class UnmanagedBlockBodiesTests
{
    [Test]
    public void Should_dispose_memory_owner()
    {
        IMemoryOwner<byte> memoryOwner = Substitute.For<IMemoryOwner<byte>>();
        BlockBody[] blockBodies = new[] { Build.A.Block.WithTransactions(2, MainnetSpecProvider.Instance).TestObject.Body };
        UnmanagedBlockBodies unmanagedBlockBodies = new UnmanagedBlockBodies(blockBodies, memoryOwner);
        unmanagedBlockBodies.Dispose();
        memoryOwner.Received().Dispose();
    }
}
