// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingContextTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task Test_TransactionProcessed_EventIsFired(CancellationToken cancellationToken)
    {
        using PrivateKey privateKeyA = new("010102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
        using PrivateKey privateKeyB = new("020102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

        await using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .WithGenesisPostProcessor((_, state) =>
            {
                state.AddToBalanceAndCreateIfNotExists(privateKeyA.Address, 10.Ether, Osaka.Instance);
            })
            .Build();

        IMainProcessingContext mainProcessingContext = ctx.Resolve<IMainProcessingContext>();
        int totalTransactionProcessed = 0;
        mainProcessingContext.TransactionProcessed += (_, _) => totalTransactionProcessed++;

        await ctx.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);
        await ctx.Resolve<TestBlockchainUtil>().AddBlockAndWaitForHead(false, cancellationToken,
            Build.A.Transaction
                .WithGasLimit(100_000)
                .WithSenderAddress(privateKeyA.Address)
                .WithCode(Prepare.EvmCode
                    .ForInitOf(Prepare.EvmCode
                        .PushData(privateKeyB.Address)
                        .Done)
                    .Done)
                .Signed(privateKeyA)
                .TestObject);

        Assert.That(totalTransactionProcessed, Is.EqualTo(1));
    }
}
