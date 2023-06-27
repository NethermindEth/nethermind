// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Optimism;

public interface IOptimismConfig : IConfig
{
    bool Enabled { get; set; }
}
