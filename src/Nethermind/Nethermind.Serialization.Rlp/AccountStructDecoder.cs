// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public class AccountStructDecoder : IRlpObjectDecoder<AccountStruct?>, IRlpStreamDecoder<AccountStruct?>, IRlpValueDecoder<AccountStruct?>
{
    private readonly bool _slimFormat;

    public static AccountStructDecoder Instance => new();
    public static AccountStructDecoder Slim => new(slimFormat: true);

    public AccountStructDecoder() { }

    public AccountStructDecoder(bool slimFormat = false)
    {
        _slimFormat = slimFormat;
    }

    public (ValueHash256 CodeHash, ValueHash256 StorageRoot) DecodeHashesOnly(ref Rlp.ValueDecoderContext context)
    {
        context.SkipLength();
        context.SkipItem();
        context.SkipItem();

        ValueHash256 storageRoot = DecodeStorageRoot(ref context);
        ValueHash256 codeHash = DecodeCodeHash(ref context);

        return (codeHash, storageRoot);
    }

    public AccountStruct? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = rlpStream.ReadSequenceLength();
        if (length == 1)
        {
            return null;
        }

        UInt256 nonce = rlpStream.DecodeUInt256();
        UInt256 balance = rlpStream.DecodeUInt256();
        ValueHash256 storageRoot = DecodeStorageRoot(rlpStream);
        ValueHash256 codeHash = DecodeCodeHash(rlpStream);
        if (storageRoot == ValueKeccak.EmptyTreeHash && codeHash == ValueKeccak.OfAnEmptyString)
        {
            return new(nonce, balance);
        }

        return new(nonce, balance, storageRoot, codeHash);
    }

    public void Encode(RlpStream stream, AccountStruct? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is not {} notNull)
        {
            stream.EncodeNullObject();
            return;
        }

        Encode(notNull, stream);
    }

    public Rlp Encode(AccountStruct? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is not {} notNull)
        {
            return Rlp.OfEmptySequence;
        }

        int contentLength = GetContentLength(notNull);
        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

        Encode(notNull, rlpStream, contentLength);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(AccountStruct account, RlpStream rlpStream, int? contentLength = null)
    {
        contentLength ??= GetContentLength(account);

        rlpStream.StartSequence(contentLength.Value);
        rlpStream.Encode(account.Nonce);
        rlpStream.Encode(account.Balance);

        if (_slimFormat && !account.HasStorage)
        {
            rlpStream.EncodeEmptyByteArray();
        }
        else
        {
            rlpStream.Encode(account.StorageRoot);
        }

        if (_slimFormat && !account.HasCode)
        {
            rlpStream.EncodeEmptyByteArray();
        }
        else
        {
            rlpStream.Encode(account.CodeHash);
        }
    }

    public int GetLength(AccountStruct? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return 1;
        }

        return Rlp.LengthOfSequence(GetContentLength(item));
    }

    public int GetContentLength(AccountStruct? item)
    {
        if (item is not {} notNull)
        {
            return 0;
        }

        return GetContentLength(notNull);
    }

    public int GetContentLength(AccountStruct item)
    {
        var contentLength = Rlp.LengthOf(item.Nonce);
        contentLength += Rlp.LengthOf(item.Balance);

        if (_slimFormat && !item.HasStorage)
        {
            contentLength++;
        }
        else
        {
            contentLength += Rlp.LengthOfKeccakRlp;
        }

        if (_slimFormat && !item.HasCode)
        {
            contentLength++;
        }
        else
        {
            contentLength += Rlp.LengthOfKeccakRlp;
        }

        return contentLength;
    }

    private ValueHash256 DecodeStorageRoot(RlpStream rlpStream)
    {
        ValueHash256 storageRoot;
        if (_slimFormat && rlpStream.IsNextItemEmptyArray())
        {
            rlpStream.ReadByte();
            storageRoot = ValueKeccak.EmptyTreeHash;
        }
        else
        {
            storageRoot = rlpStream.DecodeKeccak()!;
        }

        return storageRoot;
    }

    private ValueHash256 DecodeCodeHash(RlpStream rlpStream)
    {
        ValueHash256 codeHash;
        if (_slimFormat && rlpStream.IsNextItemEmptyArray())
        {
            rlpStream.ReadByte();
            codeHash = ValueKeccak.OfAnEmptyString;
        }
        else
        {
            codeHash = rlpStream.DecodeKeccak();
        }

        return codeHash;
    }

    public AccountStruct? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        if (length == 1)
        {
            return null;
        }

        UInt256 nonce = decoderContext.DecodeUInt256();
        UInt256 balance = decoderContext.DecodeUInt256();
        ValueHash256 storageRoot = DecodeStorageRoot(ref decoderContext);
        ValueHash256 codeHash = DecodeCodeHash(ref decoderContext);
        if (storageRoot == ValueKeccak.EmptyTreeHash && codeHash == ValueKeccak.OfAnEmptyString)
        {
            return new(nonce, balance);
        }

        return new(nonce, balance, storageRoot, codeHash);
    }

    private ValueHash256 DecodeStorageRoot(ref Rlp.ValueDecoderContext rlpStream)
    {
        if (_slimFormat && rlpStream.IsNextItemEmptyArray())
        {
            rlpStream.ReadByte();
            return ValueKeccak.EmptyTreeHash;
        }

        return rlpStream.DecodeValueKeccakNonNull();
    }

    private ValueHash256 DecodeCodeHash(ref Rlp.ValueDecoderContext rlpStream)
    {
        if (_slimFormat && rlpStream.IsNextItemEmptyArray())
        {
            rlpStream.ReadByte();
            return ValueKeccak.OfAnEmptyString;
        }

        return rlpStream.DecodeValueKeccakNonNull();
    }
}
