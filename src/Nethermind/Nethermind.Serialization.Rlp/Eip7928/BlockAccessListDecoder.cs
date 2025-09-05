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
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public BlockAccessList Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        AccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance);
        if (accountChanges.Length > Eip7928Constants.MaxAccounts)
        {
            throw new RlpException("Number of accounts exceeded maximum.");
        }

        SortedDictionary<Address, AccountChanges> accountChangesMap = new(accountChanges.ToDictionary(a => a.Address, a => a));
        BlockAccessList blockAccessList = new(accountChangesMap);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return blockAccessList;
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
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.EncodeArray([.. item.GetAccountChanges()]);
    }

    private static int GetContentLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int len = 0;

        foreach (AccountChanges accountChange in item.GetAccountChanges())
        {
            len += AccountChangesDecoder.Instance.GetLength(accountChange, rlpBehaviors);
        }

        return Rlp.LengthOfSequence(len);
    }
}
