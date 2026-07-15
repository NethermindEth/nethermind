// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core.Exceptions;
using Nethermind.Db;

namespace Nethermind.State.Pbt;

public class PbtPlugin(IPbtConfig config, IFlatDbConfig flatDbConfig) : INethermindPlugin
{
    public string Name => "Pbt";
    public string Description => "EIP-8297 partitioned binary tree state backend";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;

    public IModule? Module => flatDbConfig.Enabled
        ? throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.Enabled)} and {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)} are mutually exclusive", -1)
        : new PbtModule();
}
