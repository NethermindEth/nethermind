// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class AllocationJson
    {
        public BuiltInJson BuiltIn { get; set; }

        public UInt256? Balance { get; set; }

        public UInt256 Nonce { get; set; }

        public byte[] Code { get; set; }

        public byte[] Constructor { get; set; }
        public Dictionary<string, string> Storage { get; set; }

        public Dictionary<UInt256, byte[]> GetConvertedStorage()
        {
            return Storage?.ToDictionary(s => Bytes.FromHexString(s.Key).ToUInt256(), s => Bytes.FromHexString(s.Value));
        }
    }
}
