// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;

namespace Nethermind.ExternalSigner.Plugin;

public class ClefSignerPlugin(IMiningConfig miningConfig) : INethermindPlugin
{
    public string Name => "Clef signer";

    public string Description => "Enabled signing from a remote Clef instance over Json RPC.";

    public string Author => "Nethermind";

    public bool Enabled => !string.IsNullOrEmpty(miningConfig.Signer);

    public IModule Module => new ClefSignerModule(miningConfig);
}
