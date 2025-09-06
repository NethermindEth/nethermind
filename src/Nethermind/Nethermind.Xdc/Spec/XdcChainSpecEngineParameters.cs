// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Spec;
internal class XdcChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.XDPoS;
    public int Epoch { get; set; }
    public int Gap { get; set; }
    public int Period { get; set; }
    public bool SkipV1Validation { get; set; }
    public Address FoundationWalletAddr { get; set; }
    public int Reward { get; set; }
}

