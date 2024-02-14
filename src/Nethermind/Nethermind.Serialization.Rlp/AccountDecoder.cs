// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class AccountDecoder : IRlpObjectDecoder<Account?>, IRlpStreamDecoder<Account?>, IRlpValueDecoder<Account?>
    {
        private readonly bool _slimFormat;

        public static AccountDecoder Instance => new();

        public AccountDecoder() { }

        public AccountDecoder(bool slimFormat = false)
        {
            _slimFormat = slimFormat;
        }

        public (Hash256 CodeHash, Hash256 StorageRoot) DecodeHashesOnly(RlpStream rlpStream)
        {
            rlpStream.SkipLength();
            rlpStream.SkipItem();
            rlpStream.SkipItem();

            Hash256 storageRoot = DecodeStorageRoot(rlpStream);
            Hash256 codeHash = DecodeCodeHash(rlpStream);

            return (codeHash, storageRoot);
        }

        public Hash256 DecodeStorageRootOnly(ref Rlp.ValueDecoderContext context)
        {
            context.SkipLength();
            context.SkipItem();
            context.SkipItem();
            Hash256 storageRoot = DecodeStorageRoot(context);
            return storageRoot;
        }

        public Account? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = rlpStream.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }

            UInt256 nonce = rlpStream.DecodeUInt256();
            UInt256 balance = rlpStream.DecodeUInt256();
            Hash256 storageRoot = DecodeStorageRoot(rlpStream);
            Hash256 codeHash = DecodeCodeHash(rlpStream);
            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        public void Encode(RlpStream stream, Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            Encode(item, stream);
        }

        public Rlp Encode(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            int contentLength = GetContentLength(item);
            RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

            Encode(item, rlpStream, contentLength);

            return new Rlp(rlpStream.Data.ToArray());
        }

        public void Encode(Account account, RlpStream rlpStream, int? contentLength = null)
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

        public int GetLength(Account[] accounts)
        {
            int length = 0;

            if (accounts is null || accounts.Length == 0)
            {
                return 1;
            }

            for (int i = 0; i < accounts.Length; i++)
            {
                length += GetLength(accounts[i]);
            }

            return length;
        }

        public int GetLength(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item));
        }

        public int GetContentLength(Account? item)
        {
            if (item is null)
            {
                return 0;
            }

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

        private Hash256 DecodeStorageRoot(RlpStream rlpStream)
        {
            Hash256 storageRoot;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                storageRoot = Keccak.EmptyTreeHash;
            }
            else
            {
                storageRoot = rlpStream.DecodeKeccak()!;
            }

            return storageRoot;
        }

        private Hash256 DecodeStorageRoot(Rlp.ValueDecoderContext context)
        {
            Hash256 storageRoot;
            if (_slimFormat && context.IsNextItemEmptyArray())
            {
                context.ReadByte();
                storageRoot = Keccak.EmptyTreeHash;
            }
            else
            {
                storageRoot = context.DecodeKeccak()!;
            }

            return storageRoot;
        }


        private Hash256 DecodeCodeHash(RlpStream rlpStream)
        {
            Hash256 codeHash;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                codeHash = Keccak.OfAnEmptyString;
            }
            else
            {
                codeHash = rlpStream.DecodeKeccak();
            }

            return codeHash;
        }

        public Account? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = decoderContext.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }

            UInt256 nonce = decoderContext.DecodeUInt256();
            UInt256 balance = decoderContext.DecodeUInt256();
            Hash256 storageRoot = DecodeStorageRoot(ref decoderContext);
            Hash256 codeHash = DecodeCodeHash(ref decoderContext);
            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        private Hash256 DecodeStorageRoot(ref Rlp.ValueDecoderContext rlpStream)
        {
            Hash256 storageRoot;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                storageRoot = Keccak.EmptyTreeHash;
            }
            else
            {
                storageRoot = rlpStream.DecodeKeccak();
            }

            return storageRoot;
        }

        private Hash256 DecodeCodeHash(ref Rlp.ValueDecoderContext rlpStream)
        {
            Hash256 codeHash;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                codeHash = Keccak.OfAnEmptyString;
            }
            else
            {
                codeHash = rlpStream.DecodeKeccak();
            }

            return codeHash;
        }

        private ValueHash256 DecodeStorageRootStruct(ref Rlp.ValueDecoderContext rlpStream)
        {
            ValueHash256 storageRoot;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                storageRoot = Keccak.EmptyTreeHash.ValueHash256;
            }
            else
            {
                storageRoot = rlpStream.DecodeValueKeccak()!.Value;
            }

            return storageRoot;
        }

        private ValueHash256 DecodeCodeHashStruct(ref Rlp.ValueDecoderContext rlpStream)
        {
            ValueHash256 codeHash;
            if (_slimFormat && rlpStream.IsNextItemEmptyArray())
            {
                rlpStream.ReadByte();
                codeHash = Keccak.OfAnEmptyString.ValueHash256;
            }
            else
            {
                codeHash = rlpStream.DecodeValueKeccak()!.Value;
            }

            return codeHash;
        }

        public AccountStruct? DecodeStruct(ref Rlp.ValueDecoderContext decoderContext)
        {
            int length = decoderContext.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }

            UInt256 nonce = decoderContext.DecodeUInt256();
            UInt256 balance = decoderContext.DecodeUInt256();
            ValueHash256 storageRoot = DecodeStorageRootStruct(ref decoderContext);
            ValueHash256 codeHash = DecodeCodeHashStruct(ref decoderContext);
            return new AccountStruct(nonce, balance, storageRoot, codeHash);
        }
    }
}
