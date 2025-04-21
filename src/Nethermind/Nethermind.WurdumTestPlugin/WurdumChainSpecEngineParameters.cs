// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.WurdumTestPlugin;

public class WurdumChainSpecEngineParameters : IChainSpecEngineParameters
{
    public const string WurdumEngineName = "Wurdum";

    public string? EngineName => SealEngineType;
    public string? SealEngineType => WurdumEngineName;
}
