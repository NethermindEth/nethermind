// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api;

public class PluginPriorities
{
    public const int HealthChecks = 0;
    public const int Clique = 10;
    public const int Aura = 20;
    public const int Ethash = 30;
    public const int Optimism = 40;
    public const int Shutter = 50;
    public const int Taiko = 60;
    public const int Merge = 70;
    public const int Flashbots = 80;
    public const int Hive = 90;
    public const int Default = 1000;
}
