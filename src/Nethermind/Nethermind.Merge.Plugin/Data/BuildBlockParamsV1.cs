// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data;

public class BuildBlockParamsV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, Transaction[] txs, byte[]? extraData)
{
    public Hash256 ParentBlockHash { get; set; } = parentBlockHash;
    public PayloadAttributes PayloadAttributes { get; set; } = payloadAttributes;
    public Transaction[] Transactions { get; set; } = txs;
    public byte[]? ExtraData { get; set; } = extraData;
}
