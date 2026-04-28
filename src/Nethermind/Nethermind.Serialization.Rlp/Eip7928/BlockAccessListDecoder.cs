// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : IRlpValueDecoder<BlockAccessList>, IRlpStreamEncoder<BlockAccessList>
{
    private static BlockAccessListDecoder? _instance = null;
    public static BlockAccessListDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public int GetLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public BlockAccessList Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        AccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, true, default, _accountsLimit);

        Address? lastAddress = null;
        int itemCount = 0;
        SortedDictionary<Address, AccountChanges> accountChangesMap = new(GenericComparer.GetOptimized<Address>());
        foreach (AccountChanges a in accountChanges)
        {
            Address address = a.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                throw new RlpException("Account changes were in incorrect order.");
            }
            lastAddress = address;
            accountChangesMap.Add(address, a);
            itemCount += 1 + a.StorageChanges.Count + a.StorageReads.Count;
        }

        BlockAccessList blockAccessList = new(accountChangesMap) { ItemCount = itemCount };
        return blockAccessList;
    }

    public void Encode(RlpStream stream, BlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => stream.EncodeArray([.. item.AccountChanges], rlpBehaviors);

    private static int GetContentLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
        => AccountChangesDecoder.Instance.GetContentLength([.. item.AccountChanges], rlpBehaviors);
}
