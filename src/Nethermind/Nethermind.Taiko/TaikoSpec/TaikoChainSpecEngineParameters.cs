// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.Taiko;
    public long? OntakeTransition { get; set; }
    public long? PacayaTransition { get; set; }
}
