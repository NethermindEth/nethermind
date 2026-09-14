// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
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
            LiteRlpReader reader = new(context.Data);
            int position = SkipToHashes(reader, context.Position);

            position = DecodeHash(reader, position, Keccak.EmptyTreeHash, out Hash256 storageRoot);
            context.Position = DecodeHash(reader, position, Keccak.OfAnEmptyString, out Hash256 codeHash);

            return (codeHash, storageRoot);
        }

        public Hash256 DecodeStorageRootOnly(ref RlpReader context)
        {
            LiteRlpReader reader = new(context.Data);
            context.Position = DecodeHash(reader, SkipToHashes(reader, context.Position), Keccak.EmptyTreeHash, out Hash256 storageRoot);
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
            LiteRlpReader reader = new(accountRlp);
            DecodeHash(reader, SkipToHashes(reader, 0), Keccak.EmptyTreeHash, out Hash256 storageRoot);
            return storageRoot;
        }

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <remarks><inheritdoc cref="DecodeStorageRootOnly(ReadOnlySpan{byte})" path="/remarks"/></remarks>
        public bool TryDecodeStruct(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
            => TryDecodeStruct(new(accountRlp), position: 0, out _, out account);

        /// <summary>The cursor-threaded core both public overloads run.</summary>
        /// <param name="reader">The buffer to decode from.</param>
        /// <param name="position">Offset of the account sequence within <paramref name="reader"/>.</param>
        /// <param name="endPosition">Offset just past the account, or just past the sequence prefix of a placeholder.</param>
        /// <param name="account">The decoded account, or <see cref="AccountStruct.TotallyEmpty"/> for a placeholder.</param>
        /// <returns><see langword="true"/> when an account was decoded; otherwise <see langword="false"/>.</returns>
        private bool TryDecodeStruct(LiteRlpReader reader, int position, out int endPosition, out AccountStruct account)
        {
            reader.ReadSequenceLength(ref position, out int length);
            if (length == 1)
            {
                account = AccountStruct.TotallyEmpty;
                endPosition = position;
                return false;
            }

            reader.DecodeULong(ref position, out ulong nonce);
            reader.DecodeUInt256(ref position, out UInt256 balance);
            position = DecodeValueHash(reader, position, Keccak.EmptyTreeHash.ValueHash256, out ValueHash256 storageRoot);
            endPosition = DecodeValueHash(reader, position, Keccak.OfAnEmptyString.ValueHash256, out ValueHash256 codeHash);

            account = new AccountStruct(nonce, balance, storageRoot, codeHash);
            return true;
        }

        /// <summary>Skips the sequence header, the nonce and the balance.</summary>
        private static int SkipToHashes(LiteRlpReader reader, int position)
        {
            reader.SkipLength(ref position);
            reader.SkipItems(ref position, 2);
            return position;
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
            LiteRlpReader reader = new(decoderContext.Data);
            int position = decoderContext.Position;
            reader.ReadSequenceLength(ref position, out int length);
            if (length == 1)
            {
                decoderContext.Position = position;
                return null;
            }

            reader.DecodeULong(ref position, out ulong nonce);
            reader.DecodeUInt256(ref position, out UInt256 balance);
            position = DecodeHash(reader, position, Keccak.EmptyTreeHash, out Hash256 storageRoot);
            decoderContext.Position = DecodeHash(reader, position, Keccak.OfAnEmptyString, out Hash256 codeHash);

            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        /// <summary>Decodes an account hash, taking the slim format's empty byte string as <paramref name="slimEmpty"/>.</summary>
        /// <remarks>
        /// Force-inlined so the caller's read of <paramref name="slimEmpty"/> sinks back into the slim
        /// branch that uses it, rather than being paid on every account.
        /// </remarks>
        /// <returns>The position past the item.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeHash(LiteRlpReader reader, int position, Hash256 slimEmpty, out Hash256 hash)
        {
            if (IsSlimEmpty(reader, position))
            {
                hash = slimEmpty;
                return position + 1;
            }

            reader.DecodeKeccak(ref position, out hash);
            return position;
        }

        /// <inheritdoc cref="DecodeHash"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeValueHash(LiteRlpReader reader, int position, in ValueHash256 slimEmpty, out ValueHash256 hash)
        {
            if (IsSlimEmpty(reader, position))
            {
                hash = slimEmpty;
                return position + 1;
            }

            reader.DecodeValueKeccakNonNull(ref position, out hash);
            return position;
        }

        private bool IsSlimEmpty(LiteRlpReader reader, int position)
            => _slimFormat && reader.Data[position] == Rlp.EmptyByteArrayByte;

        /// <summary>Decodes an account payload into its allocation-free <see cref="AccountStruct"/> form.</summary>
        /// <remarks>
        /// Returns <see langword="false"/> only for the placeholder encoding that carries no account — a
        /// sequence with a single content byte — leaving <paramref name="account"/> as
        /// <see cref="AccountStruct.TotallyEmpty"/> and the reader positioned just past the sequence prefix.
        /// Malformed input is not reported this way: it throws <see cref="RlpException"/> like any other decode.
        /// </remarks>
        /// <returns><see langword="true"/> when an account was decoded; otherwise <see langword="false"/>.</returns>
        public bool TryDecodeStruct(ref RlpReader decoderContext, out AccountStruct account)
        {
            bool decoded = TryDecodeStruct(new(decoderContext.Data), decoderContext.Position, out int endPosition, out account);
            decoderContext.Position = endPosition;
            return decoded;
        }
    }
}
