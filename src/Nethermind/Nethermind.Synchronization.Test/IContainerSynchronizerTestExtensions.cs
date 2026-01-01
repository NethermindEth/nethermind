// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;

namespace Nethermind.Synchronization.Test;

public static class IContainerSynchronizerTestExtensions
{
    public static ContainerBuilder WithSuggestedHeaderOfStateRoot(this ContainerBuilder builder, Hash256 stateRoot)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1ul).TestObject;
        // State sync pivot uses the convenience overload FindHeader(ulong), which is implemented as a default
        // interface method and then forwards to FindHeader(ulong, BlockTreeLookupOptions). Make sure both are stubbed.
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(header);
        blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        blockTree.FindBestSuggestedHeader().Returns(header);
        blockTree.BestSuggestedHeader.Returns(header);
        blockTree.SyncPivot.Returns((0ul, Keccak.Zero));

        builder.AddSingleton(blockTree);

        return builder;
    }
}
