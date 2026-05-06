// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BlockAccessListDecoder :
    IRlpValueDecoder<ReadOnlyBlockAccessList>,
    IRlpStreamEncoder<ReadOnlyBlockAccessList>,
    IRlpStreamEncoder<GeneratedBlockAccessList>
{
    private static BlockAccessListDecoder? _instance;
    public static BlockAccessListDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _accountsLimit = new(Eip7928Constants.MaxAccounts, "", ReadOnlyMemory<char>.Empty);

    public ReadOnlyBlockAccessList Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        ReadOnlyAccountChanges[] accountChanges = ctx.DecodeArray(AccountChangesDecoder.Instance, true, default, _accountsLimit);

        Address? lastAddress = null;
        int itemCount = 0;
        foreach (ReadOnlyAccountChanges a in accountChanges)
        {
            // EIP-7928 AccountChanges is a 6-field sequence; an empty inner
            // list (RLP 0xc0) is rejected by DecodeArray as defaultElement -> null.
            if (a is null)
            {
                throw new RlpException("Empty AccountChanges entry; EIP-7928 requires a 6-field sequence.");
            }

            Address address = a.Address;
            if (lastAddress is not null && address.CompareTo(lastAddress) <= 0)
            {
                throw new RlpException("Account changes were in incorrect order.");
            }
            lastAddress = address;
            itemCount += 1 + a.StorageChanges.Length + a.StorageReads.Length;
        }

        return new ReadOnlyBlockAccessList(accountChanges, itemCount);
    }

    public int GetLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int len = ReadOnlyAccountsContentLength(item, rlpBehaviors);
        stream.StartSequence(len);
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            AccountChangesDecoder.Instance.Encode(stream, a, rlpBehaviors);
        }
    }

    public void Encode(RlpStream stream, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int len = GeneratedAccountsContentLength(item, rlpBehaviors);
        stream.StartSequence(len);
        foreach (GeneratedAccountChanges a in item.AccountChanges)
        {
            AccountChangesDecoder.Instance.Encode(stream, a, rlpBehaviors);
        }
    }

    private static int GetContentLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
        => ReadOnlyAccountsContentLength(item, rlpBehaviors);

    private static int GetContentLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
        => GeneratedAccountsContentLength(item, rlpBehaviors);

    private static int ReadOnlyAccountsContentLength(ReadOnlyBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (ReadOnlyAccountChanges a in item.AccountChanges)
        {
            len += AccountChangesDecoder.Instance.GetLength(a, rlpBehaviors);
        }
        return len;
    }

    private static int GeneratedAccountsContentLength(GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (GeneratedAccountChanges a in item.AccountChanges)
        {
            len += AccountChangesDecoder.Instance.GetLength(a, rlpBehaviors);
        }
        return len;
    }
}
