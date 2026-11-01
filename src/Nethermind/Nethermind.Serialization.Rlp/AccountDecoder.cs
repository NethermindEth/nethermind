// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
        private const byte PlaceholderPrefix = Rlp.EmptyListByte + 1;
        private const byte OneByteLengthListPrefix = 0xf8;
        private const byte HashPrefix = Rlp.EmptyByteArrayByte + Hash256.Size;
        private const int MaxShortLength = 55;
        private const int UInt256Bytes = 32;
        private const int MaxStorageRootPosition = 1 + sizeof(ulong) + 1 + UInt256Bytes;

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
        public Hash256 DecodeStorageRootOnly(ReadOnlySpan<byte> accountRlp) => DecodeStorageRootOnly<OnFlag>(accountRlp);

        /// <inheritdoc cref="DecodeStorageRootOnly(ReadOnlySpan{byte})"/>
        /// <inheritdoc cref="TryDecodeStruct{TCanonicalChecks}(ref RlpReader, out AccountStruct)" path="/typeparam"/>
        [SkipLocalsInit]
        public Hash256 DecodeStorageRootOnly<TCanonicalChecks>(ReadOnlySpan<byte> accountRlp) where TCanonicalChecks : struct, IFlag
        {
            // The integers are at most 9 and 33 bytes long here, so with a longer payload every prefix up to the storage
            // root is in bounds, and only the storage root needs a check.
            if (TryEnterAccount<TCanonicalChecks>(accountRlp, out int start, out int length) && length > MaxStorageRootPosition)
            {
                ref byte content = ref Unsafe.Add(ref MemoryMarshal.GetReference(accountRlp), (nint)(uint)start);
                int position = 0;
                if (TrySkipInteger<TCanonicalChecks>(ref content, ref position, sizeof(ulong))
                    && TrySkipInteger<TCanonicalChecks>(ref content, ref position, UInt256Bytes))
                {
                    byte prefix = Unsafe.Add(ref content, (nint)(uint)position);
                    if (prefix == HashPrefix && length - position > Hash256.Size)
                    {
                        return RlpReaderExtensions.InternKeccak(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref content, (nint)(uint)position + 1), Hash256.Size));
                    }

                    if (prefix == Rlp.EmptyByteArrayByte && _slimFormat) return Keccak.EmptyTreeHash;
                }
            }

            return DecodeStorageRootOnlyWithReader(accountRlp);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Hash256 DecodeStorageRootOnlyWithReader(ReadOnlySpan<byte> accountRlp)
        {
            RlpReader reader = new(accountRlp);
            RlpReader fields = reader.EnterSequence(out _);
            SkipToHashes(ref fields);
            return DecodeHash(ref fields, Keccak.EmptyTreeHash);
        }

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <remarks>The bytes after the account are not checked.</remarks>
        public bool TryDecodeStruct(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
            => TryDecodeStruct<OnFlag>(accountRlp, out account);

        /// <inheritdoc cref="TryDecodeStruct{TCanonicalChecks}(ref RlpReader, out AccountStruct)"/>
        /// <remarks>The bytes after the account are not checked.</remarks>
        [SkipLocalsInit]
        public bool TryDecodeStruct<TCanonicalChecks>(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
            where TCanonicalChecks : struct, IFlag
        {
            if (TrySplit<TCanonicalChecks>(accountRlp, out ulong nonce, out int balance, out int balanceLength, out int storageRoot, out int codeHash))
            {
                account = CreateStruct(accountRlp, nonce, balance, balanceLength, storageRoot, codeHash);
                return true;
            }

            return TryDecodeStructWithReader<TCanonicalChecks>(accountRlp, out account);
        }

        // Out of line, so that the reader local does not make the fast path zero its stack frame.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryDecodeStructWithReader<TCanonicalChecks>(ReadOnlySpan<byte> accountRlp, out AccountStruct account)
            where TCanonicalChecks : struct, IFlag
        {
            RlpReader reader = new(accountRlp);
            return TryDecodeStruct<TCanonicalChecks>(ref reader, out account);
        }

        /// <summary>Skips the nonce and the balance.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SkipToHashes(ref RlpReader fields)
        {
            fields.ReadBytes();
            fields.ReadBytes();
        }

        /// <summary>Reads the placeholder that carries no account: a sequence with a single content byte.</summary>
        /// <remarks>
        /// Only the prefix is read, and the content byte is left for the next read, as the old decoder did. So a
        /// complete decode of the placeholder fails, and the byte after the prefix is not checked.
        /// </remarks>
        /// <returns><see langword="true"/> when the placeholder was read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadPlaceholder(ref RlpReader reader)
        {
            if (reader.PeekByte() != PlaceholderPrefix) return false;

            reader.ReadRaw(1);
            return true;
        }

        /// <summary>Splits an account in one pass over its bytes, as offsets into the item.</summary>
        /// <remarks>
        /// Every canonical account fits the forms read here: a list with a length of at most 255 bytes, integers of at
        /// most 8 and 32 bytes, and 32-byte hashes or, in the slim format, <c>0x80</c>. Each prefix is checked as
        /// <see cref="RlpReader"/> checks it in the same mode, and the list must hold the four fields and nothing else.
        /// For any other input it returns <see langword="false"/>, and <see cref="SplitWithReader"/> reads or rejects
        /// the input. Offsets rather than spans keep the fast path free of tracked references, so its frame is not zeroed.
        /// </remarks>
        /// <param name="item">The whole account item.</param>
        /// <param name="nonce">The nonce.</param>
        /// <param name="balance">The offset of the big-endian bytes of the balance.</param>
        /// <param name="balanceLength">The count of the big-endian bytes of the balance.</param>
        /// <param name="storageRoot">The offset of the 32 bytes of the storage root, or -1 for the slim empty form.</param>
        /// <param name="codeHash">The offset of the 32 bytes of the code hash, or -1 for the slim empty form.</param>
        /// <returns><see langword="true"/> when the input is a canonical account.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TrySplit<TCanonicalChecks>(ReadOnlySpan<byte> item, out ulong nonce, out int balance, out int balanceLength, out int storageRoot, out int codeHash)
            where TCanonicalChecks : struct, IFlag
        {
            nonce = 0;
            balance = balanceLength = 0;
            storageRoot = codeHash = -1;
            if (!TryEnterAccount<TCanonicalChecks>(item, out int start, out int length)) return false;

            ReadOnlySpan<byte> content = item.Slice(start, length);
            int position = 0;
            if (!TryReadInteger<TCanonicalChecks>(content, ref position, sizeof(ulong), out int nonceStart, out int nonceLength)
                || !TryReadInteger<TCanonicalChecks>(content, ref position, UInt256Bytes, out balance, out balanceLength)
                || !TryReadHash(content, ref position, out storageRoot)
                || !TryReadHash(content, ref position, out codeHash)
                || position != length)
            {
                return false;
            }

            nonce = RlpReaderExtensions.Word(content, nonceStart, nonceStart + nonceLength);
            balance += start;
            if (storageRoot >= 0) storageRoot += start;
            if (codeHash >= 0) codeHash += start;
            return true;
        }

        /// <summary>Splits an account with <see cref="RlpReader"/>, for the input that <see cref="TrySplit"/> does not read.</summary>
        /// <inheritdoc cref="TrySplit" path="/param"/>
        /// <exception cref="RlpReaderException">The item is not a canonical account.</exception>
        private void SplitWithReader(ReadOnlySpan<byte> item, out ulong nonce, out int balance, out int balanceLength, out int storageRoot, out int codeHash)
        {
            RlpReader reader = new(item);
            RlpReader fields = reader.EnterSequence(out int itemLength);
            int start = item.Length - fields.Remaining;
            nonce = fields.DecodeULong();
            ReadOnlySpan<byte> balanceBytes = fields.ReadIntegerBytes();
            if (balanceBytes.Length > UInt256Bytes) throw new RlpReaderException("RLP integer is too large");
            balance = start + fields.BytesRead - balanceBytes.Length;
            balanceLength = balanceBytes.Length;
            storageRoot = ReadHashOffset(ref fields, start);
            codeHash = ReadHashOffset(ref fields, start);
            reader.ExitSequence(in fields, itemLength);
        }

        private int ReadHashOffset(scoped ref RlpReader fields, int start)
        {
            if (_slimFormat && fields.TryReadEmptyBytes()) return -1;

            fields.ReadBytes(Hash256.Size);
            return start + fields.BytesRead - Hash256.Size;
        }

        /// <summary>Gets the payload of an account list that has a length of at most 255 bytes and is not the placeholder.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryEnterAccount<TCanonicalChecks>(ReadOnlySpan<byte> item, out int start, out int length)
            where TCanonicalChecks : struct, IFlag
        {
            start = 1;
            length = 0;
            if (item.Length < 2) return false;

            int prefix = At(item, 0);
            if (prefix == OneByteLengthListPrefix)
            {
                start = 2;
                length = At(item, 1);
                if (TCanonicalChecks.IsActive && length <= MaxShortLength) return false;
            }
            else if (prefix is > PlaceholderPrefix and < OneByteLengthListPrefix)
            {
                length = prefix - Rlp.EmptyListByte;
            }
            else
            {
                return false;
            }

            return length <= item.Length - start;
        }

        /// <summary>Reads a canonical integer of at most <paramref name="maxLength"/> bytes.</summary>
        /// <param name="start">The offset of the big-endian bytes.</param>
        /// <param name="length">The count of the big-endian bytes; zero for zero.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadInteger<TCanonicalChecks>(ReadOnlySpan<byte> content, scoped ref int position, int maxLength, out int start, out int length)
            where TCanonicalChecks : struct, IFlag
        {
            start = position;
            length = 1;
            if ((uint)position >= (uint)content.Length) return false;

            int prefix = At(content, position);
            if (prefix < Rlp.EmptyByteArrayByte)
            {
                if (TCanonicalChecks.IsActive && prefix == 0) return false;
                position++;
                return true;
            }

            length = prefix - Rlp.EmptyByteArrayByte;
            start = position + 1;
            if (length > maxLength || length > content.Length - start) return false;
            if (TCanonicalChecks.IsActive && length != 0)
            {
                byte first = At(content, start);
                if (first == 0 || (length == 1 && first < Rlp.EmptyByteArrayByte)) return false;
            }

            position = start + length;
            return true;
        }

        /// <summary>Moves past an integer of at most <paramref name="maxLength"/> bytes; the prefix is checked, the payload is not.</summary>
        /// <remarks>The caller makes sure that the bytes up to <paramref name="position"/> + 1 + <paramref name="maxLength"/> are in bounds.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySkipInteger<TCanonicalChecks>(ref byte content, scoped ref int position, int maxLength)
            where TCanonicalChecks : struct, IFlag
        {
            int prefix = Unsafe.Add(ref content, (nint)(uint)position);
            if (prefix < Rlp.EmptyByteArrayByte)
            {
                position++;
                return true;
            }

            int length = prefix - Rlp.EmptyByteArrayByte;
            if (length > maxLength) return false;
            if (TCanonicalChecks.IsActive && length == 1 && Unsafe.Add(ref content, (nint)(uint)position + 1) < Rlp.EmptyByteArrayByte) return false;

            position += 1 + length;
            return true;
        }

        /// <summary>Reads a 32-byte hash or, in the slim format, <c>0x80</c>.</summary>
        /// <param name="hash">The offset of the 32 bytes of the hash, or -1 for <c>0x80</c>.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryReadHash(ReadOnlySpan<byte> content, scoped ref int position, out int hash)
        {
            hash = -1;
            if ((uint)position >= (uint)content.Length) return false;

            int prefix = At(content, position);
            if (prefix == HashPrefix)
            {
                int start = position + 1;
                if (content.Length - start < Hash256.Size) return false;
                hash = start;
                position = start + Hash256.Size;
                return true;
            }

            if (prefix != Rlp.EmptyByteArrayByte || !_slimFormat) return false;
            position++;
            return true;
        }

        /// <summary>Reads the byte at <paramref name="index"/>, which the caller checked against the length of <paramref name="bytes"/>.</summary>
        /// <remarks>The fast path checks every index itself, so the bounds check of the span indexer would be a second one.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte At(ReadOnlySpan<byte> bytes, int index)
        {
            Debug.Assert((uint)index < (uint)bytes.Length);
            return Unsafe.Add(ref MemoryMarshal.GetReference(bytes), (nint)(uint)index);
        }

        /// <summary>Builds the balance from its <paramref name="length"/> big-endian bytes at <paramref name="start"/>.</summary>
        /// <remarks>
        /// The words go into the result as one vector. The callers copy the balance with 32-byte loads, and such a load
        /// cannot take its data from four 8-byte stores that are still in the store buffer, so it waits for them.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 ReadBalance(ReadOnlySpan<byte> item, int start, int length)
        {
            if (length == UInt256Bytes) return RlpReaderExtensions.ToUInt256(item.Slice(start, UInt256Bytes));

            int end = start + length;
            if (length <= sizeof(ulong) && end >= sizeof(ulong))
            {
                // Most balances fit in one word. One load that ends at the balance reads it; the mask drops the bytes
                // before it, which are other fields of the item.
                ulong word = BinaryPrimitives.ReverseEndianness(
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(item), (nint)(uint)(end - sizeof(ulong)))));
                ulong value = length == sizeof(ulong) ? word : word & ((1UL << (length * 8)) - 1);
                return Vector256.IsHardwareAccelerated
                    ? Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.CreateScalar(value))
                    : new UInt256(value);
            }

            int end1 = Math.Max(end - sizeof(ulong), start);
            int end2 = Math.Max(end1 - sizeof(ulong), start);
            int end3 = Math.Max(end2 - sizeof(ulong), start);
            ulong u0 = RlpReaderExtensions.Word(item, end1, end);
            ulong u1 = RlpReaderExtensions.Word(item, end2, end1);
            ulong u2 = RlpReaderExtensions.Word(item, end3, end2);
            ulong u3 = RlpReaderExtensions.Word(item, start, end3);
            return Vector256.IsHardwareAccelerated
                ? Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.Create(u0, u1, u2, u3))
                : new UInt256(u0, u1, u2, u3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static AccountStruct CreateStruct(ReadOnlySpan<byte> item, ulong nonce, int balance, int balanceLength, int storageRoot, int codeHash)
            => new(
                nonce,
                ReadBalance(item, balance, balanceLength),
                storageRoot < 0 ? Keccak.EmptyTreeHash.ValueHash256 : new ValueHash256(item.Slice(storageRoot, Hash256.Size)),
                codeHash < 0 ? Keccak.OfAnEmptyString.ValueHash256 : new ValueHash256(item.Slice(codeHash, Hash256.Size)));

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
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader) : Decode<OnFlag>(ref reader);

        [SkipLocalsInit]
        private Account? Decode<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
        {
            if (TryReadPlaceholder(ref reader))
            {
                return null;
            }

            ReadOnlySpan<byte> item = reader.ReadEncodedItem<TCanonicalChecks>();
            return TrySplit<TCanonicalChecks>(item, out ulong nonce, out int balanceStart, out int balanceLength, out int storageRootStart, out int codeHashStart)
                ? CreateAccount(item, nonce, balanceStart, balanceLength, storageRootStart, codeHashStart)
                : CreateAccountWithReader(item);
        }

        // Out of line, so that the out arguments of the fast path need no address and stay in registers.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Account CreateAccountWithReader(ReadOnlySpan<byte> item)
        {
            SplitWithReader(item, out ulong nonce, out int balanceStart, out int balanceLength, out int storageRootStart, out int codeHashStart);
            return CreateAccount(item, nonce, balanceStart, balanceLength, storageRootStart, codeHashStart);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Account CreateAccount(ReadOnlySpan<byte> item, ulong nonce, int balanceStart, int balanceLength, int storageRootStart, int codeHashStart)
        {
            UInt256 balance = ReadBalance(item, balanceStart, balanceLength);
            if (storageRootStart < 0 && codeHashStart < 0)
            {
                return new(nonce, balance);
            }

            Hash256 storageRoot = storageRootStart < 0 ? Keccak.EmptyTreeHash : RlpReaderExtensions.InternKeccak(item.Slice(storageRootStart, Hash256.Size));
            Hash256 codeHash = codeHashStart < 0 ? Keccak.OfAnEmptyString : RlpReaderExtensions.InternKeccak(item.Slice(codeHashStart, Hash256.Size));
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

        /// <summary>Decodes an account payload into its allocation-free <see cref="AccountStruct"/> form.</summary>
        /// <remarks>
        /// Returns <see langword="false"/> only for the placeholder encoding that carries no account — a
        /// sequence with a single content byte — leaving <paramref name="account"/> as
        /// <see cref="AccountStruct.TotallyEmpty"/> and the reader positioned just past the sequence prefix.
        /// Malformed input is not reported this way: it throws <see cref="RlpReaderException"/> like any other decode.
        /// </remarks>
        /// <returns><see langword="true"/> when an account was decoded; otherwise <see langword="false"/>.</returns>
        public bool TryDecodeStruct(ref RlpReader reader, out AccountStruct account)
            => TryDecodeStruct<OnFlag>(ref reader, out account);

        /// <inheritdoc cref="TryDecodeStruct(ref RlpReader, out AccountStruct)"/>
        /// <typeparam name="TCanonicalChecks">
        /// <see cref="OnFlag"/> for untrusted input. <see cref="OffFlag"/> only for bytes that this node encoded and
        /// stored itself: it skips the checks of the canonical form, such as leading zero bytes, but keeps every
        /// bounds and length check, so that corrupted bytes still throw <see cref="RlpReaderException"/>.
        /// </typeparam>
        [SkipLocalsInit]
        public bool TryDecodeStruct<TCanonicalChecks>(ref RlpReader reader, out AccountStruct account)
            where TCanonicalChecks : struct, IFlag
        {
            if (TryReadPlaceholder(ref reader))
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            ReadOnlySpan<byte> item = reader.ReadEncodedItem<TCanonicalChecks>();
            account = TrySplit<TCanonicalChecks>(item, out ulong nonce, out int balance, out int balanceLength, out int storageRoot, out int codeHash)
                ? CreateStruct(item, nonce, balance, balanceLength, storageRoot, codeHash)
                : CreateStructWithReader(item);
            return true;
        }

        // Out of line, so that the out arguments of the fast path need no address and stay in registers.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private AccountStruct CreateStructWithReader(ReadOnlySpan<byte> item)
        {
            SplitWithReader(item, out ulong nonce, out int balance, out int balanceLength, out int storageRoot, out int codeHash);
            return CreateStruct(item, nonce, balance, balanceLength, storageRoot, codeHash);
        }
    }
}
