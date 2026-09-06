// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            ReadOnlySpan<byte> data = context.Data;
            int position = SkipToHashes(data, context.Position);

            position = DecodeStorageRoot(data, position, out Hash256 storageRoot);
            context.Position = DecodeCodeHash(data, position, out Hash256 codeHash);

            return (codeHash, storageRoot);
        }

        public Hash256 DecodeStorageRootOnly(ref RlpReader context)
        {
            ReadOnlySpan<byte> data = context.Data;
            context.Position = DecodeStorageRoot(data, SkipToHashes(data, context.Position), out Hash256 storageRoot);
            return storageRoot;
        }

        /// <summary>Reads the storage root straight out of an account payload.</summary>
        /// <remarks>
        /// Saves the caller an <see cref="RlpReader"/> whose only job would be to carry a cursor for one
        /// call. The reader is a ~40-byte ref struct that is address-exposed once passed by reference,
        /// so constructing one is a real run of stores.
        /// </remarks>
        public Hash256 DecodeStorageRootOnly(ReadOnlySpan<byte> accountRlp)
        {
            DecodeStorageRoot(accountRlp, SkipToHashes(accountRlp, 0), out Hash256 storageRoot);
            return storageRoot;
        }

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <remarks><inheritdoc cref="DecodeStorageRootOnly(ReadOnlySpan{byte})" path="/remarks"/></remarks>
        public bool TryDecodeStruct(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
        {
            int position = RlpHelpers.ReadSequenceLength(accountRlp, 0, out int length);
            if (length == 1)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            position = RlpHelpers.DecodeULong(accountRlp, position, out ulong nonce);
            position = RlpHelpers.DecodeUInt256(accountRlp, position, -1, out UInt256 balance);
            position = DecodeStorageRootStruct(accountRlp, position, out ValueHash256 storageRoot);
            DecodeCodeHashStruct(accountRlp, position, out ValueHash256 codeHash);

            account = new AccountStruct(nonce, balance, storageRoot, codeHash);
            return true;
        }

        /// <summary>Skips the sequence header, the nonce and the balance.</summary>
        private static int SkipToHashes(ReadOnlySpan<byte> data, int position)
            => RlpHelpers.SkipItems(data, RlpHelpers.SkipLength(data, position), 2);

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

        /// <summary>
        /// Encodes a non-null <paramref name="account"/> into a freshly allocated <see cref="byte"/> array.
        /// </summary>
        /// <remarks>
        /// Computes the content length once and reuses it for both sizing the buffer and writing the
        /// sequence header, avoiding the double <see cref="GetContentLength"/> pass that the generic
        /// <see cref="RlpDecoder{T}.EncodeAsBytes"/> incurs. The buffer is allocated uninitialized
        /// because encoding fills it completely.
        /// </remarks>
        public byte[] EncodeAsBytes(Account account)
        {
            int contentLength = GetContentLength(account);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(Rlp.LengthOfSequence(contentLength));
            RlpWriter writer = new(bytes);
            Encode(account, ref writer, contentLength);
            return bytes;
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
            ReadOnlySpan<byte> data = decoderContext.Data;
            int position = DecodeStorageRoot(data, decoderContext.Position, out Hash256 storageRoot);
            decoderContext.Position = DecodeCodeHash(data, position, out Hash256 codeHash);

            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        private int DecodeStorageRoot(ReadOnlySpan<byte> data, int position, out Hash256 storageRoot)
        {
            if (IsSlimEmpty(data, position))
            {
                storageRoot = Keccak.EmptyTreeHash;
                return position + 1;
            }

            return RlpHelpers.DecodeKeccak(data, position, out storageRoot);
        }

        private int DecodeCodeHash(ReadOnlySpan<byte> data, int position, out Hash256 codeHash)
        {
            if (IsSlimEmpty(data, position))
            {
                codeHash = Keccak.OfAnEmptyString;
                return position + 1;
            }

            return RlpHelpers.DecodeKeccak(data, position, out codeHash);
        }

        private int DecodeStorageRootStruct(ReadOnlySpan<byte> data, int position, out ValueHash256 storageRoot)
        {
            if (IsSlimEmpty(data, position))
            {
                storageRoot = Keccak.EmptyTreeHash.ValueHash256;
                return position + 1;
            }

            return RlpHelpers.DecodeValueKeccakNonNull(data, position, out storageRoot);
        }

        private int DecodeCodeHashStruct(ReadOnlySpan<byte> data, int position, out ValueHash256 codeHash)
        {
            if (IsSlimEmpty(data, position))
            {
                codeHash = Keccak.OfAnEmptyString.ValueHash256;
                return position + 1;
            }

            return RlpHelpers.DecodeValueKeccakNonNull(data, position, out codeHash);
        }

        private bool IsSlimEmpty(ReadOnlySpan<byte> data, int position)
            => _slimFormat && data[position] == Rlp.EmptyByteArrayByte;

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
            ReadOnlySpan<byte> data = decoderContext.Data;
            int position = DecodeStorageRootStruct(data, decoderContext.Position, out ValueHash256 storageRoot);
            decoderContext.Position = DecodeCodeHashStruct(data, position, out ValueHash256 codeHash);

            account = new AccountStruct(nonce, balance, storageRoot, codeHash);
            return true;
        }
    }
}
