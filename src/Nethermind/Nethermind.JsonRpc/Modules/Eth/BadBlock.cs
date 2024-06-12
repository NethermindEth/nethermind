// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

public class BadBlock(Block block, bool includeFullTransactionData, ISpecProvider specProvider, BlockDecoder blockDecoder)
{
    public BlockForRpc Block { get; } = new BlockForRpc(block, includeFullTransactionData, specProvider);
    public Hash256 Hash { get; } = block.Header.Hash;
    public byte[] Rlp { get; } = blockDecoder.Encode(block).Bytes;
}
