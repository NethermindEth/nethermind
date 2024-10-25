// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.Era1.Test;

public class EraTestModule : Module
{
    public const string TestNetwork = "abc";

    public static ContainerBuilder BuildContainerBuilder()
    {
        return new ContainerBuilder().AddModule(new EraTestModule());
    }

    public static ContainerBuilder BuildContainerBuilderWithBlockTreeOfLength(int length)
    {
        return new ContainerBuilder()
            .AddModule(new EraTestModule())
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(length).TestObject);
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new EraModule())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<ISpecProvider>(Substitute.For<ISpecProvider>())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<TmpDirectory>()
            .AddKeyedSingleton<ITunableDb>(DbNames.Receipts, Substitute.For<ITunableDb>())
            .AddKeyedSingleton<ITunableDb>(DbNames.Blocks, Substitute.For<ITunableDb>())
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                MaxEra1Size = 16,
            })
            .AddSingleton<IBlockTree>(Build.A.BlockTree().TestObject)
            .AddKeyedSingleton(EraComponentKeys.NetworkName, TestNetwork)
            .AddSingleton<IReceiptStorage>(Substitute.For<IReceiptStorage>());
    }
}
