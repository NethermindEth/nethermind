// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko;

public class TaikoNethermindApi(
    IConfigProvider configProvider,
    IJsonSerializer jsonSerializer,
    ILogManager logManager,
    ChainSpec chainSpec) : NethermindApi(configProvider, jsonSerializer, logManager, chainSpec)
{
    public InvalidChainTracker? InvalidChainTracker { get; set; }
}
