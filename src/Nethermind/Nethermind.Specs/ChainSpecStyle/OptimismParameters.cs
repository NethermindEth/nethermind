// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class OptimismParameters
    {
        public ulong RegolithTimestamp { get; set; }

        public long BedrockBlockNumber { get; set; }
    }
}
