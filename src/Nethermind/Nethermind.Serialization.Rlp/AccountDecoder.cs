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
            ReadOnlySpan<byte> data = context.Data;
            int position = SkipToHashes(data, context.Position);

            position = DecodeHash(data, position, Keccak.EmptyTreeHash, out Hash256 storageRoot);
            context.Position = DecodeHash(data, position, Keccak.OfAnEmptyString, out Hash256 codeHash);

            return (codeHash, storageRoot);
        }

        public Hash256 DecodeStorageRootOnly(ref RlpReader context)
        {
            ReadOnlySpan<byte> data = context.Data;
            context.Position = DecodeHash(data, SkipToHashes(data, context.Position), Keccak.EmptyTreeHash, out Hash256 storageRoot);
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
            DecodeHash(accountRlp, SkipToHashes(accountRlp, 0), Keccak.EmptyTreeHash, out Hash256 storageRoot);
            return storageRoot;
        }

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <remarks><inheritdoc cref="DecodeStorageRootOnly(ReadOnlySpan{byte})" path="/remarks"/></remarks>
        public bool TryDecodeStruct(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
            => TryDecodeStruct(accountRlp, position: 0, out _, out account);

        /// <summary>The cursor-threaded core both public overloads run.</summary>
        /// <param name="data">The buffer to decode from.</param>
        /// <param name="position">Offset of the account sequence within <paramref name="data"/>.</param>
        /// <param name="endPosition">Offset just past the account, or just past the sequence prefix of a placeholder.</param>
        /// <param name="account">The decoded account, or <see cref="AccountStruct.TotallyEmpty"/> for a placeholder.</param>
        /// <returns><see langword="true"/> when an account was decoded; otherwise <see langword="false"/>.</returns>
        private bool TryDecodeStruct(ReadOnlySpan<byte> data, int position, out int endPosition, out AccountStruct account)
        {
            position = RlpHelpers.ReadSequenceLength(data, position, out int length);
            if (length == 1)
            {
                account = AccountStruct.TotallyEmpty;
                endPosition = position;
                return false;
            }

            position = RlpHelpers.DecodeULong(data, position, out ulong nonce);
            position = RlpHelpers.DecodeUInt256(data, position, out UInt256 balance);
            position = DecodeValueHash(data, position, Keccak.EmptyTreeHash.ValueHash256, out ValueHash256 storageRoot);
            endPosition = DecodeValueHash(data, position, Keccak.OfAnEmptyString.ValueHash256, out ValueHash256 codeHash);

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
            ReadOnlySpan<byte> data = decoderContext.Data;
            int position = RlpHelpers.ReadSequenceLength(data, decoderContext.Position, out int length);
            if (length == 1)
            {
                decoderContext.Position = position;
                return null;
            }

            position = RlpHelpers.DecodeULong(data, position, out ulong nonce);
            position = RlpHelpers.DecodeUInt256(data, position, out UInt256 balance);
            position = DecodeHash(data, position, Keccak.EmptyTreeHash, out Hash256 storageRoot);
            decoderContext.Position = DecodeHash(data, position, Keccak.OfAnEmptyString, out Hash256 codeHash);

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
        private int DecodeHash(ReadOnlySpan<byte> data, int position, Hash256 slimEmpty, out Hash256 hash)
        {
            if (IsSlimEmpty(data, position))
            {
                hash = slimEmpty;
                return position + 1;
            }

            return RlpHelpers.DecodeKeccak(data, position, out hash);
        }

        /// <inheritdoc cref="DecodeHash"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeValueHash(ReadOnlySpan<byte> data, int position, in ValueHash256 slimEmpty, out ValueHash256 hash)
        {
            if (IsSlimEmpty(data, position))
            {
                hash = slimEmpty;
                return position + 1;
            }

            return RlpHelpers.DecodeValueKeccakNonNull(data, position, out hash);
        }

        private bool IsSlimEmpty(ReadOnlySpan<byte> data, int position)
            => _slimFormat && data[position] == Rlp.EmptyByteArrayByte;

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
            bool decoded = TryDecodeStruct(decoderContext.Data, decoderContext.Position, out int endPosition, out account);
            decoderContext.Position = endPosition;
            return decoded;
        }
    }
}
