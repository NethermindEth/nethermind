// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public class MevBundleRpc
    {
        public byte[][] Txs { get; set; } = Array.Empty<byte[]>();
        public long BlockNumber { get; set; }
        public UInt256? MinTimestamp { get; set; } = null;
        public UInt256? MaxTimestamp { get; set; } = null;
        public Keccak[]? RevertingTxHashes { get; set; } = null;
    }
}
