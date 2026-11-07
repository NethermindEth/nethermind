// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Init;

namespace Nethermind.InvalidChainDump;

/// <summary>
/// Uploads the diagnostics of invalid blocks as zip archives to S3-compatible storage.
/// </summary>
public class InvalidChainDumpPlugin(IInvalidChainDumpConfig config) : INethermindPlugin
{
    public string Name => "InvalidChainDump";
    public string Description => "Uploads invalid-block diagnostics to S3-compatible storage.";
    public string Author => "Nethermind";
    public bool Enabled => !string.IsNullOrWhiteSpace(config.ServiceUrl);
    public IModule Module => new InvalidChainDumpModule();
}

public class InvalidChainDumpModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .Intercept<IInitConfig>(static initConfig => initConfig.AutoDump |= DumpOptions.All)
        .AddSingleton<IDumpArchiveStore, S3DumpArchiveStore>()
        .AddSingleton<IInvalidBlockDumpHandler, InvalidChainDumpUploader>();
}
