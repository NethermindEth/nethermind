// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingContextTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task Test_TransactionProcessed_EventIsFired(CancellationToken cancelationToken)
    {
        await using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .WithGenesisPostProcessor((_, state) =>
            {
                state.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, 10.Ether(), Osaka.Instance);
            })
            .Build();

        var mainProcessingContext = ctx.Resolve<IMainProcessingContext>();
        int totalTransactionProcessed = 0;
        mainProcessingContext.TransactionProcessed += (_, _) => totalTransactionProcessed++;

        await ctx.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancelationToken);
        await ctx.Resolve<TestBlockchainUtil>().AddBlockAndWaitForHead(false, cancelationToken,
            Build.A.Transaction
                .WithGasLimit(100_000)
                .WithSenderAddress(TestItem.AddressA)
                .WithCode(Prepare.EvmCode
                    .ForInitOf(Prepare.EvmCode
                        .PushData(TestItem.PrivateKeyB.Address)
                        .Done)
                    .Done)
                .Signed(TestItem.PrivateKeyA)
                .TestObject);

        totalTransactionProcessed.Should().Be(1);
    }
}
