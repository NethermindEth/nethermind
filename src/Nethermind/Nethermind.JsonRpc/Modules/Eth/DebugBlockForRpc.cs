// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Eth;

public class DebugBlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, HeaderDecoder headerDecoder) : BlockForRpc(block, includeFullTransactionData, specProvider)
{
    public byte[] Rlp { get; } = headerDecoder.Encode(block.Header).Bytes;
}
