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
        // var tmp = ctx.Data[ctx.Position..].ToArray();
        // Console.WriteLine("bal:");
        // Console.WriteLine(Bytes.ToHexString(tmp));

        AccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance);
        if (accountChanges.Length > Eip7928Constants.MaxAccounts)
        {
            throw new RlpException("Number of accounts exceeded maximum.");
        }

        Address? lastAddress = null;
        SortedDictionary<Address, AccountChanges> accountChangesMap = new(accountChanges.ToDictionary(a =>
        {
            Address address = a.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                throw new RlpException("Account changes were in incorrect order.");
            }
            lastAddress = address;
            return address;
        }, a => a));
        BlockAccessList blockAccessList = new(accountChangesMap);

        // if (!accountChanges.SequenceEqual(accountChangesMap.Values))
        // {
        //     throw new RlpException("Accounts were in incorrect order.");
        // }

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
        stream.EncodeArray([.. item.GetAccountChanges()], rlpBehaviors);
    }

    private static int GetContentLength(BlockAccessList item, RlpBehaviors rlpBehaviors)
        => AccountChangesDecoder.Instance.GetContentLength([.. item.GetAccountChanges()], rlpBehaviors);
}
