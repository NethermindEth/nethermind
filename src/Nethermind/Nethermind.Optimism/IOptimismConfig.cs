// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Optimism;

public interface IOptimismConfig : IConfig
{
    bool Enabled { get; set; }

    long RegolithBlockNumber { get; set; }

    long BedrockBlockNumber { get; set; }

    Address L1FeeReceiver { get; set; }
}
