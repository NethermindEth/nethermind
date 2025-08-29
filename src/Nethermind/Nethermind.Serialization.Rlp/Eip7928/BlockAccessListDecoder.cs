// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder : IRlpValueDecoder<BlockAccessList>, IRlpStreamDecoder<BlockAccessList>
{
    private static BlockAccessListDecoder? _instance = null;
    public static BlockAccessListDecoder Instance => _instance ??= new();

    public int GetLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
        => GetContentLength(item, rlpBehaviors);

    public BlockAccessList Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        AccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance);
        SortedDictionary<Address, AccountChanges> accountChangesMap = new(accountChanges.ToDictionary(a => new Address(a.Address), a => a));
        return new()
        {
            AccountChanges = accountChangesMap
        };
    }

    public BlockAccessList Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        BlockAccessList res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, BlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.EncodeArray([.. item.AccountChanges.Values]);
        // foreach (AccountChanges accountChanges in item.AccountChanges.Values)
        // {
        //     AccountChangesDecoder.Instance.Encode(stream, accountChanges);
        // }
    }

    private static int GetContentLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int len = 0;

        foreach (AccountChanges accountChange in item.AccountChanges.Values)
        {
            len += AccountChangesDecoder.Instance.GetLength(accountChange, rlpBehaviors);
        }

        return len;
    }
}
