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
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);
        blockTree.BestSuggestedHeader.Returns(header);

        builder.AddSingleton(blockTree);

        return builder;
    }
}
