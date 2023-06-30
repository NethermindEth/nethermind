// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State;

public interface IStateTree : IPatriciaTree
{
    Account? Get(Address address, Keccak? rootHash = null);

    void Set(Address address, Account? account);
    Rlp? Set(Keccak keccak, Account? account);

    public byte[] GetStorage(in UInt256 index, in Address accountAddress,  Keccak? root = null);

    public void SetStorage(in UInt256 index, byte[] value, in Address accountAddress);

    public void SetStorage(Keccak key, byte[] value, in Address accountAddress, bool rlpEncode = true);
}
