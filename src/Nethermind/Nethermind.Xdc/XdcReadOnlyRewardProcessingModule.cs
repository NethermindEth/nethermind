// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core.Container;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

internal sealed class XdcReadOnlyRewardProcessingModule : Module, IBlockValidationModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(c => new ReadOnlyRewardsStore(c.Resolve<RewardsStore>()))
            .As<IRewardsStore>()
            .SingleInstance();

        builder.Register(_ => new ReadOnlyMintedRecordContract())
            .As<IMintedRecordContract>()
            .SingleInstance();
    }
}
