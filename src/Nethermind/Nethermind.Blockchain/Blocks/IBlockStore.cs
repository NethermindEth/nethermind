// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Blocks;

/// <summary>
/// Raw block store. Does not know or care about blockchain or blocktree, only encoding/decoding to kv store.
/// </summary>
public interface IBlockStore
{
    void Insert(Block block);
    void Delete(Keccak blockHash);
    Block Get(Keccak blockHash, bool shouldCache = true);
    void Cache(Block block);


    // These two are used by blocktree. Try not to use them...
    void SetMetadata(byte[] key, byte[] value);
    byte[]? GetMetadata(byte[] key);
}
