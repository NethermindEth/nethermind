// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcBlockValidationModule : Module, IBlockValidationModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddScoped<IBlockProcessor.IBlockTransactionsExecutor, XdcBlockValidationTransactionExecutor>();
    }
}
