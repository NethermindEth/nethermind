// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko;

public class TaikoChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => TaikoPlugin.Taiko;
    public string? SealEngineType => TaikoPlugin.Taiko;
}
