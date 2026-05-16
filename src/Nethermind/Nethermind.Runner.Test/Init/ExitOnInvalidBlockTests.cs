// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Init;

public class ExitOnInvalidBlockTests
{
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public void InvalidBlock_TriggersExit_OnlyWhenConfigured(bool exitOnInvalidBlock, int expectedExitCalls)
    {
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new InitConfig
            {
                ExitOnInvalidBlock = exitOnInvalidBlock
            }))
            .AddSingleton<IProcessExitSource>(processExitSource)
            .Build();

        IBlockchainProcessor processor = container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        RaiseInvalidBlock(processor);

        processExitSource.Received(expectedExitCalls).Exit(ExitCodes.InvalidBlock);
    }

    private static void RaiseInvalidBlock(IBlockchainProcessor processor)
    {
        FieldInfo field = processor.GetType().GetField(nameof(IBlockchainProcessor.InvalidBlock), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"InvalidBlock event field not found on {processor.GetType().FullName}");
        EventHandler<IBlockchainProcessor.InvalidBlockEventArgs>? handler = (EventHandler<IBlockchainProcessor.InvalidBlockEventArgs>?)field.GetValue(processor);
        handler?.Invoke(processor, new IBlockchainProcessor.InvalidBlockEventArgs { InvalidBlock = Build.A.Block.TestObject });
    }
}
