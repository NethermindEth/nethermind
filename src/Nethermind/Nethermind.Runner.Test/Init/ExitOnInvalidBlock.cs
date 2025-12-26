// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Init;

public class ExitOnInvalidBlockTests
{
    [Test]
    public void CallExit_OnInvalidBlock()
    {
        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(Substitute.For<IBlockchainProcessor>());

        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new InitConfig()
            {
                ExitOnInvalidBlock = true
            }))
            .AddSingleton<IMainProcessingContext>(mainProcessingContext)
            .AddSingleton<IProcessExitSource>(processExitSource)
            .Build();

        container.Resolve<ExitOnInvalidBlock>().Execute(default);

        mainProcessingContext.BlockchainProcessor.InvalidBlock += Raise.EventWith(null, new IBlockchainProcessor.InvalidBlockEventArgs());

        processExitSource.Received().Exit(-1);
    }
}
