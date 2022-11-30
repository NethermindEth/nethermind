// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    public class CliqueParameters
    {
        public ulong Epoch { get; set; }

        public ulong Period { get; set; }

        public UInt256? Reward { get; set; }
    }
}
