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
    public bool Enabled => config.Enabled || config.MigrationEnabled || chainSpec.Parameters.Eip8347TransitionTimestamp is not null;

    /// <remarks>
    /// The two backends are alternatives, so enabling both is a misconfiguration — unless PBT is
    /// asked to mirror the flat one, which is the one mode that needs both.
    /// </remarks>
    public IModule? Module
    {
        get
        {
            PbtMigrationConfigValidator.Validate(config, flatDbConfig, chainSpec, initConfig.BaseDbPath);
            if (config.MigrationEnabled)
                return new PbtMigrationModule(config);
            if (chainSpec.Parameters.Eip8347TransitionTimestamp is { } activation &&
                (!config.Enabled || config.MirrorFlat || config.FakeMatchingStateRoot || chainSpec.Genesis is null || activation > chainSpec.Genesis.Timestamp))
                throw new InvalidConfigurationException("Scheduled binaryTrieTime requires an available EIP-8347 migration backend.", -1);
            return CreateModule();
        }
    }

    private IModule CreateModule() => config.MirrorFlat
        ? flatDbConfig.Enabled
            ? new PbtMirrorModule(config)
            : throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.MirrorFlat)} mirrors the flat state backend, so it requires {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)}", -1)
        : flatDbConfig.Enabled
            ? throw new InvalidConfigurationException($"{nameof(IPbtConfig)}.{nameof(IPbtConfig.Enabled)} and {nameof(IFlatDbConfig)}.{nameof(IFlatDbConfig.Enabled)} are mutually exclusive", -1)
            : new PbtModule(config);
}
