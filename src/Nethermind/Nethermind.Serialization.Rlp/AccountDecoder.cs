// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public sealed class AccountDecoder : RlpDecoder<Account?>
    {
        private readonly bool _slimFormat;

        public static AccountDecoder Instance { get; } = new();
        public static AccountDecoder Slim { get; } = new(slimFormat: true);

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AccountDecoder))]
        public AccountDecoder() { }

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AccountDecoder))]
        public AccountDecoder(bool slimFormat = false) => _slimFormat = slimFormat;

        public (Hash256 CodeHash, Hash256 StorageRoot) DecodeHashesOnly(ref RlpReader context)
        {
            context.SkipLength();
            context.SkipItem();
            context.SkipItem();

            Hash256 storageRoot = DecodeStorageRoot(ref context);
            Hash256 codeHash = DecodeCodeHash(ref context);

            return (codeHash, storageRoot);
        }

        public Hash256 DecodeStorageRootOnly(ref RlpReader context)
        {
            context.SkipLength();
            context.SkipItem();
            context.SkipItem();
            Hash256 storageRoot = DecodeStorageRoot(ref context);
            return storageRoot;
        }

        public override void Encode<TWriter>(ref TWriter writer, Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            Encode(item, ref writer);
        }

        public void Encode<TWriter>(Account account, ref TWriter writer, int? contentLength = null)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            contentLength ??= GetContentLength(account);

            writer.StartSequence(contentLength.Value);
            writer.Encode(account.Nonce);
            writer.Encode(account.Balance);

            if (_slimFormat && !account.HasStorage)
            {
                writer.EncodeEmptyByteArray();
            }
            else
            {
                writer.Encode(account.StorageRoot);
            }

            if (_slimFormat && !account.HasCode)
            {
                writer.EncodeEmptyByteArray();
            }
            else
            {
                writer.Encode(account.CodeHash);
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

        public override int GetLength(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

            int contentLength = Rlp.LengthOf(item.Nonce);
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

        protected override Account? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = decoderContext.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }

            ulong nonce = decoderContext.DecodeULong();
            UInt256 balance = decoderContext.DecodeUInt256();
            Hash256 storageRoot = DecodeStorageRoot(ref decoderContext);
            Hash256 codeHash = DecodeCodeHash(ref decoderContext);
            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        private Hash256 DecodeStorageRoot(ref RlpReader reader)
        {
            Hash256 storageRoot;
            if (_slimFormat && reader.IsNextItemEmptyByteArray())
            {
                reader.ReadByte();
                storageRoot = Keccak.EmptyTreeHash;
            }
            else
            {
                storageRoot = reader.DecodeKeccak();
            }

            return storageRoot;
        }

        private Hash256 DecodeCodeHash(ref RlpReader reader)
        {
            Hash256 codeHash;
            if (_slimFormat && reader.IsNextItemEmptyByteArray())
            {
                reader.ReadByte();
                codeHash = Keccak.OfAnEmptyString;
            }
            else
            {
                codeHash = reader.DecodeKeccak();
            }

            return codeHash;
        }

        private ValueHash256 DecodeStorageRootStruct(ref RlpReader reader)
        {
            ValueHash256 storageRoot;
            if (_slimFormat && reader.IsNextItemEmptyByteArray())
            {
                reader.ReadByte();
                storageRoot = Keccak.EmptyTreeHash.ValueHash256;
            }
            else
            {
                storageRoot = reader.DecodeValueKeccak()!.Value;
            }

            return storageRoot;
        }

        private ValueHash256 DecodeCodeHashStruct(ref RlpReader reader)
        {
            ValueHash256 codeHash;
            if (_slimFormat && reader.IsNextItemEmptyByteArray())
            {
                reader.ReadByte();
                codeHash = Keccak.OfAnEmptyString.ValueHash256;
            }
            else
            {
                codeHash = reader.DecodeValueKeccak()!.Value;
            }

            return codeHash;
        }

        public bool TryDecodeStruct(ref RlpReader decoderContext, out AccountStruct account)
        {
            int length = decoderContext.ReadSequenceLength();
            if (length == 1)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            ulong nonce = decoderContext.DecodeULong();
            UInt256 balance = decoderContext.DecodeUInt256();
            ValueHash256 storageRoot = DecodeStorageRootStruct(ref decoderContext);
            ValueHash256 codeHash = DecodeCodeHashStruct(ref decoderContext);
            account = new AccountStruct(nonce, balance, storageRoot, codeHash);
            return true;
        }
    }
}
