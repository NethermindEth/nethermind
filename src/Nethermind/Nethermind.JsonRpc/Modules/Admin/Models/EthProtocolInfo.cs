// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin.Models
{
    public class EthProtocolInfo
    {
        public UInt256 Difficulty { get; set; }
        public Hash256 GenesisHash { get; set; }
        public Hash256 HeadHash { get; set; }
        public ulong NetworkId { get; set; }
        public ulong ChainId { get; set; }
        public ChainParameters Config { get; set; }
    }
}
