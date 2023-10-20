// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismNethermindApi : NethermindApi
{
    public OptimismNethermindApi(
        IConfigProvider configProvider,
        IJsonSerializer jsonSerializer,
        ILogManager logManager,
        ChainSpec chainSpec) : base(configProvider, jsonSerializer, logManager, chainSpec)
    {
    }

    public IInvalidChainTracker? InvalidChainTracker { get; set; }
}
