// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api;

public class PluginPriorities
{
    // Merge have higher priority than Mev as it wraps block producer to determine when to run.
    public const int Merge = 1000;

    // Mev have higher priority than other plugin as it wraps the block production of other plugin.
    public const int Mev = 200;
}
