// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
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

        /// <summary>Reads the code hash and the storage root of an account, and moves past the account.</summary>
        public (Hash256 CodeHash, Hash256 StorageRoot) DecodeHashesOnly(ref RlpReader reader)
        {
            RlpReader fields = reader.EnterSequence(out int itemLength);
            SkipToHashes(ref fields);

            Hash256 storageRoot = DecodeHash(ref fields, Keccak.EmptyTreeHash);
            Hash256 codeHash = DecodeHash(ref fields, Keccak.OfAnEmptyString);
            reader.ExitSequence(in fields, itemLength);

            return (codeHash, storageRoot);
        }

        /// <summary>Reads the storage root of an account, and moves past the account.</summary>
        public Hash256 DecodeStorageRootOnly(ref RlpReader reader)
        {
            RlpReader fields = reader.EnterSequence(out int itemLength);
            SkipToHashes(ref fields);
            Hash256 storageRoot = DecodeHash(ref fields, Keccak.EmptyTreeHash);
            fields.SkipItem();
            reader.ExitSequence(in fields, itemLength);
            return storageRoot;
        }

        /// <summary>Reads the storage root straight out of an account payload.</summary>
        /// <remarks>Reads only the fields up to the storage root; the bytes after it are not checked.</remarks>
        public Hash256 DecodeStorageRootOnly(ReadOnlySpan<byte> accountRlp)
        {
            RlpReader reader = new(accountRlp);
            RlpReader fields = reader.EnterSequence(out _);
            SkipToHashes(ref fields);
            return DecodeHash(ref fields, Keccak.EmptyTreeHash);
        }

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <remarks>The bytes after the account are not checked.</remarks>
        public bool TryDecodeStruct(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
        {
            RlpReader reader = new(accountRlp);
            return TryDecodeStruct(ref reader, out account);
        }

        /// <summary>Skips the nonce and the balance.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SkipToHashes(ref RlpReader fields)
        {
            fields.ReadBytes();
            fields.ReadBytes();
        }

        /// <summary>Reads the placeholder that carries no account: a sequence with a single content byte.</summary>
        /// <returns><see langword="true"/> when the placeholder was read.</returns>
        private static bool TryReadPlaceholder(ref RlpReader reader, ref RlpReader fields, int itemLength)
        {
            if (fields.Remaining != 1) return false;

            fields.ReadRaw(1);
            reader.ExitSequence(in fields, itemLength);
            return true;
        }

        public override void Encode(ref RlpWriter writer, Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            Encode(item, ref writer);
        }

        public void Encode(Account account, ref RlpWriter writer, int? contentLength = null)
        {
            contentLength ??= GetContentLength(account);

            RlpSequence sequence = writer.StartSequence(contentLength.Value);
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

            writer.EndSequence(sequence);
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
        [SkipLocalsInit]
        public byte[] EncodeAsBytes(Account account)
        {
            int contentLength = GetContentLength(account);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(Rlp.LengthOfSequence(contentLength));
            // The fixed shape is written straight into the array, keeping the position in a register rather than in a
            // writer, which stores it back after every item.
            Span<byte> destination = bytes;
            int position = Rlp.StartSequence(destination, 0, contentLength);
            position = EncodeWord(destination, position, account.Nonce);
            position = EncodeWord(destination, position, account.Balance);
            position = _slimFormat && !account.HasStorage
                ? EncodeEmptyByteArray(destination, position)
                : Rlp.Encode(destination, position, account.StorageRoot);
            if (_slimFormat && !account.HasCode)
            {
                EncodeEmptyByteArray(destination, position);
            }
            else
            {
                Rlp.Encode(destination, position, account.CodeHash);
            }

            return bytes;
        }

        private static int EncodeEmptyByteArray(Span<byte> destination, int position)
        {
            destination[position] = Rlp.EmptyByteArrayByte;
            return position + 1;
        }

        /// <summary>Encodes <paramref name="value"/> as the RLP of its big-endian bytes without leading zeros.</summary>
        /// <remarks>The length comes from the top non-zero limb, rather than from a byte-wise scan for the first non-zero byte.</remarks>
        [SkipLocalsInit]
        private static int EncodeWord(Span<byte> destination, int position, in UInt256 value)
        {
            ulong top;
            int lowerLimbs;
            if (value.u3 != 0) { top = value.u3; lowerLimbs = 3; }
            else if (value.u2 != 0) { top = value.u2; lowerLimbs = 2; }
            else if (value.u1 != 0) { top = value.u1; lowerLimbs = 1; }
            else
            {
                top = value.u0;
                lowerLimbs = 0;
                if (top < Rlp.EmptyByteArrayByte)
                {
                    destination[position] = top == 0 ? Rlp.EmptyByteArrayByte : (byte)top;
                    return position + 1;
                }
            }

            int length = (lowerLimbs + 1) * sizeof(ulong) - BitOperations.LeadingZeroCount(top) / 8;
            EvmWord bigEndian = value.ToBigEndianWord();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<EvmWord>(in bigEndian));
            destination[position] = (byte)(Rlp.EmptyByteArrayByte + length);
            bytes[^length..].CopyTo(destination[(position + 1)..]);
            return position + 1 + length;
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

        protected override Account? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpReader fields = reader.EnterSequence(out int itemLength);
            if (TryReadPlaceholder(ref reader, ref fields, itemLength))
            {
                return null;
            }

            ulong nonce = fields.DecodeULong();
            UInt256 balance = fields.DecodeUInt256();
            Hash256 storageRoot = DecodeHash(ref fields, Keccak.EmptyTreeHash);
            Hash256 codeHash = DecodeHash(ref fields, Keccak.OfAnEmptyString);
            reader.ExitSequence(in fields, itemLength);

            if (ReferenceEquals(storageRoot, Keccak.EmptyTreeHash) && ReferenceEquals(codeHash, Keccak.OfAnEmptyString))
            {
                return new(nonce, balance);
            }

            return new(nonce, balance, storageRoot, codeHash);
        }

        /// <summary>Decodes an account hash, taking the slim format's empty byte string as <paramref name="slimEmpty"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Hash256 DecodeHash(ref RlpReader fields, Hash256 slimEmpty)
            => _slimFormat && fields.TryReadEmptyBytes() ? slimEmpty : fields.DecodeKeccak();

        /// <inheritdoc cref="DecodeHash"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ValueHash256 DecodeValueHash(ref RlpReader fields, in ValueHash256 slimEmpty)
            => _slimFormat && fields.TryReadEmptyBytes() ? slimEmpty : fields.DecodeValueKeccakNonNull();

        /// <summary>Decodes an account payload into its allocation-free <see cref="AccountStruct"/> form.</summary>
        /// <remarks>
        /// Returns <see langword="false"/> only for the placeholder encoding that carries no account — a
        /// sequence with a single content byte — leaving <paramref name="account"/> as
        /// <see cref="AccountStruct.TotallyEmpty"/> and the reader positioned just past the placeholder.
        /// Malformed input is not reported this way: it throws <see cref="RlpReaderException"/> like any other decode.
        /// </remarks>
        /// <returns><see langword="true"/> when an account was decoded; otherwise <see langword="false"/>.</returns>
        public bool TryDecodeStruct(ref RlpReader reader, out AccountStruct account)
        {
            RlpReader fields = reader.EnterSequence(out int itemLength);
            if (TryReadPlaceholder(ref reader, ref fields, itemLength))
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            ulong nonce = fields.DecodeULong();
            UInt256 balance = fields.DecodeUInt256();
            ValueHash256 storageRoot = DecodeValueHash(ref fields, Keccak.EmptyTreeHash.ValueHash256);
            ValueHash256 codeHash = DecodeValueHash(ref fields, Keccak.OfAnEmptyString.ValueHash256);
            reader.ExitSequence(in fields, itemLength);

            account = new AccountStruct(nonce, balance, storageRoot, codeHash);
            return true;
        }
    }
}
