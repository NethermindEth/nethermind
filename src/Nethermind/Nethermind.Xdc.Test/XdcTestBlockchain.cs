// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Blockchain.Blocks;
using Nethermind.Init.Modules;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;

namespace Nethermind.Xdc.Tests;

public class XdcTestBlockchain : TestBlockchain
{
    public override Task AddBlock(params Transaction[] transactions)
    {
        return base.AddBlock(transactions);
    }

    protected override async Task AddBlocksOnStart()
    {
        await AddBlock();
        await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
        await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);

        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (BlockTree.Head?.Number == 3) return;
            await Task.Delay(1, CancellationToken);
        }
    }
}

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder ConfigureTestConfiguration(this ContainerBuilder builder, Action<TestBlockchain.Configuration> configurer)
    {
        return builder.AddDecorator<TestBlockchain.Configuration>((ctx, conf) =>
        {
            configurer(conf);
            return conf;
        });
    }
}
