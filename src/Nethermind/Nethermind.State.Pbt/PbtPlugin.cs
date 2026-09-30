// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.Migration;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.State.Pbt;

public class PbtPlugin(IPbtConfig config, IFlatDbConfig flatDbConfig, ChainSpec chainSpec, IInitConfig initConfig) : INethermindPlugin
{
    public string Name => "Pbt";
    public string Description => "EIP-8297 partitioned binary tree state backend";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled || config.MigrationExportPath is not null || chainSpec.Parameters.Eip8347TransitionTimestamp is not null;

    /// <remarks>
    /// The chain specification picks the mode: a binaryTrieTime after genesis runs the flat backend and
    /// migrates to PBT at activation; anything else runs PBT from genesis, replacing whichever backend
    /// the core modules selected. A scheduled binaryTrieTime enables the plugin on its own so that it
    /// cannot be missed, but it still has to be opted into through <see cref="IPbtConfig.Enabled"/>.
    /// Exporting is the exception: it reads the flat state rather than replacing it, so it needs neither.
    /// </remarks>
    public IModule? Module
    {
        get
        {
            PbtMigrationConfigValidator.Validate(config, flatDbConfig, chainSpec, initConfig.BaseDbPath);
            if (config.MigrationExportPath is not null) return new PbtExportModule();
            if (!config.Enabled)
                throw new InvalidConfigurationException($"binaryTrieTime in the chain specification requires {nameof(IPbtConfig)}.{nameof(IPbtConfig.Enabled)}.", -1);
            return PbtMigrationConfigValidator.IsScheduledMigration(chainSpec) ? new PbtMigrationModule(config) : CreateModule();
        }
    }

    private IModule CreateModule() => config.MirrorFlat
        ? flatDbConfig.Enabled
            ? new PbtMirrorModule(config)
            : throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.MirrorFlat)} mirrors the flat state backend, so it requires {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)}", -1)
        : new PbtModule(config);
}
