// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class ChainSpecGenesisJson
    {
        public string Name { get; set; }
        public string DataDir { get; set; }
        public ChainSpecSealJson Seal { get; set; }
        public UInt256 Difficulty { get; set; }
        public Address Author { get; set; }
        public ulong Timestamp { get; set; }
        public Keccak ParentHash { get; set; }
        public byte[] ExtraData { get; set; }
        public UInt256 GasLimit { get; set; }

        public UInt256? BaseFeePerGas { get; set; }

        public bool StateUnavailable { get; set; } = false;
        public Keccak StateRoot { get; set; }

        public ulong? BlobGasUsed { get; set; }
        public ulong? ExcessBlobGas { get; set; }
    }
}
