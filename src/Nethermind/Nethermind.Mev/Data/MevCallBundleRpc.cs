// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;

namespace Nethermind.Mev.Data
{
    public class MevCallBundleRpc
    {
        public byte[][] Txs { get; set; } = Array.Empty<byte[]>();
        public long? BlockNumber { get; set; } = null;
        public BlockParameter StateBlockNumber { get; set; } = BlockParameter.Latest;
        public ulong? Timestamp { get; set; } = null;
    }
}
