// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.State.Pbt.Mirror;

namespace Nethermind.State.Pbt;

public class PbtPlugin(IPbtConfig config, IFlatDbConfig flatDbConfig) : INethermindPlugin
{
    public string Name => "Pbt";
    public string Description => "EIP-8297 partitioned binary tree state backend";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;

    /// <remarks>
    /// The two backends are alternatives, so enabling both is a misconfiguration — unless PBT is
    /// asked to mirror the flat one, which is the one mode that needs both.
    /// </remarks>
    public IModule? Module => config.MirrorFlat
        ? flatDbConfig.Enabled
            ? new PbtMirrorModule(config)
            : throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.MirrorFlat)} mirrors the flat state backend, so it requires {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)}", -1)
        : flatDbConfig.Enabled
            ? throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.Enabled)} and {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)} are mutually exclusive", -1)
            : new PbtModule(config);
}
