// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class ReadOnlyTxProcessorSourceTests
{
    [Test]
    public void OnScopeDispose_NextScopeShouldBeSame()
    {
        ReadOnlyTxProcessorSource txProcessorSource = new ReadOnlyTxProcessorSource(
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISpecProvider>(),
            LimboLogs.Instance
        );

        IReadOnlyTxProcessingScope scope = txProcessorSource.Build(Keccak.EmptyTreeHash);
        scope.Dispose();

        IReadOnlyTxProcessingScope scope2 = txProcessorSource.Build(Keccak.EmptyTreeHash);
        scope2.Should().BeSameAs(scope);
    }
}
