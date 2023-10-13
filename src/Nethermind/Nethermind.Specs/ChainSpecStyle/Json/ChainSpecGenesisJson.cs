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
        public Commitment ParentHash { get; set; }
        public byte[] ExtraData { get; set; }
        public UInt256 GasLimit { get; set; }

        public UInt256? BaseFeePerGas { get; set; }

        public Commitment StateRoot { get; set; }

        public ulong? BlobGasUsed { get; set; }
        public ulong? ExcessBlobGas { get; set; }
        public Commitment ParentBeaconBlockRoot { get; set; }
    }
}
