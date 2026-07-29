// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public partial class AbiType
    {
        protected const int PaddingSize = 32;
        // ABI zero-tuples have no encoded width, so their decoded count needs an explicit non-input-derived limit.
        private const int MaxZeroWidthElementsPerDecode = 16 * 1024;
        // Canonical dynamic bodies occupy distinct input regions. Charging those regions cumulatively bounds aliased tails.
        private static readonly AsyncLocal<DecodeBudget?> CurrentDecodeBudget = new();

        internal readonly struct DecodeBudgetScope : IDisposable
        {
            private readonly bool _ownsBudget;

            public DecodeBudgetScope(int encodedLength)
            {
                _ownsBudget = CurrentDecodeBudget.Value is null;
                if (_ownsBudget)
                {
                    CurrentDecodeBudget.Value = new DecodeBudget(encodedLength);
                }
            }

            public void Dispose()
            {
                if (_ownsBudget)
                {
                    CurrentDecodeBudget.Value = null;
                }
            }
        }

        private sealed class DecodeBudget(int encodedLength)
        {
            public ulong RemainingEncodedBytes { get; set; } = (uint)encodedLength;
            public int RemainingZeroWidthElements { get; set; } = MaxZeroWidthElementsPerDecode;
        }

        internal static DecodeBudgetScope EnterDecodeBudget(int encodedLength) => new(encodedLength);

        internal static void ConsumeDecodeBudget(ulong encodedBytes, AbiType? type)
        {
            DecodeBudget? budget = CurrentDecodeBudget.Value;
            if (budget is null)
            {
                return;
            }

            if (encodedBytes > budget.RemainingEncodedBytes)
            {
                string subject = type is null ? "ABI arguments" : type.ToString();
                throw new AbiException(
                    $"ABI decode allocation bound exceeded while decoding {subject}: {encodedBytes} bytes requested with {budget.RemainingEncodedBytes} bytes remaining");
            }

            budget.RemainingEncodedBytes -= encodedBytes;
        }

        internal static void ConsumeZeroWidthElementBudget(int elementCount, AbiType type)
        {
            DecodeBudget? budget = CurrentDecodeBudget.Value;
            if (budget is null)
            {
                return;
            }

            if (elementCount > budget.RemainingZeroWidthElements)
            {
                throw new AbiException(
                    $"ABI decode exceeds the supported maximum of {MaxZeroWidthElementsPerDecode} zero-width elements while decoding {type}");
            }

            budget.RemainingZeroWidthElements -= elementCount;
        }

        internal static int GetSequenceHeadSize(IReadOnlyList<AbiType> types, bool packed)
        {
            try
            {
                int headSize = 0;
                for (int i = 0; i < types.Count; i++)
                {
                    headSize = checked(headSize + types[i].GetHeadSize(packed));
                }

                return headSize;
            }
            catch (OverflowException e)
            {
                throw new AbiException("ABI sequence head size exceeds the supported maximum", e);
            }
        }

        internal static int GetRepeatedHeadSize(int length, int elementHeadSize)
        {
            try
            {
                return checked(length * elementHeadSize);
            }
            catch (OverflowException e)
            {
                throw new AbiException("ABI array head size exceeds the supported maximum", e);
            }
        }

        internal static byte[][] EncodeSequence(int length, IEnumerable<AbiType> types, IEnumerable<object?> sequence, bool packed, int offset = 0)
        {
            List<byte[]> dynamicParts = new(length);
            List<byte[]?> headerParts = new(length);
            using IEnumerator<object?> sequenceEnumerator = sequence.GetEnumerator();
            using IEnumerator<AbiType> typesEnumerator = types.GetEnumerator();
            for (int i = 0; i < length; i++)
            {
                sequenceEnumerator.MoveNext();
                typesEnumerator.MoveNext();
                object? element = sequenceEnumerator.Current;
                AbiType type = typesEnumerator.Current;

                byte[] encoded = type.Encode(element, packed);

                // encode each type
                if (type.IsDynamic)
                {
                    // offset placeholder, we cannot calculate offset before calculating all header parts
                    headerParts.Add(null);
                    dynamicParts.Add(encoded);
                }
                else
                {
                    headerParts.Add(encoded);
                }
            }

            // now lets calculate proper offset
            BigInteger currentOffset = 0;

            // offset of header
            for (int i = 0; i < headerParts.Count; i++)
            {
                currentOffset += headerParts[i]?.Length ?? PaddingSize;
            }

            // offset dynamic parts, calculating the actual offset of each part
            int dynamicPartsIndex = 0;
            for (int i = 0; i < headerParts.Count; i++)
            {
                if (headerParts[i] is null)
                {
                    headerParts[i] = UInt256.Encode(currentOffset, packed);
                    currentOffset += dynamicParts[dynamicPartsIndex++].Length;
                }
            }

            byte[][] encodedParts = new byte[offset + headerParts.Count + dynamicParts.Count][];

            for (int i = 0; i < headerParts.Count; i++)
            {
                encodedParts[offset + i] = headerParts[i]!;
            }

            for (int i = 0; i < dynamicParts.Count; i++)
            {
                encodedParts[offset + headerParts.Count + i] = dynamicParts[i];
            }

            return encodedParts;
        }

        public static (object[], int) DecodeSequence(int length, IEnumerable<AbiType> types, byte[] data, bool packed, int startPosition)
        {
            using DecodeBudgetScope decodeBudget = EnterDecodeBudget(data.Length);
            (Array array, int position) = DecodeSequence(typeof(object), length, types, data, packed, startPosition);
            return ((object[])array, position);
        }

        internal static (Array, int) DecodeSequence(Type elementType, int length, IEnumerable<AbiType> types, byte[] data, bool packed, int startPosition)
        {
            Array sequence = Array.CreateInstance(elementType, length);
            int position = startPosition;
            int dynamicPosition = 0;
            using IEnumerator<AbiType> typesEnumerator = types.GetEnumerator();
            for (int i = 0; i < length; i++)
            {
                typesEnumerator.MoveNext();
                AbiType type = typesEnumerator.Current;

                try
                {
                    object? item;

                    if (type.IsDynamic)
                    {
                        (UInt256 offset, int nextPosition) = UInt256.DecodeUInt(data, position, packed);
                        (item, dynamicPosition) = type.Decode(data, startPosition + (int)offset, packed);
                        position = nextPosition;
                    }
                    else
                    {
                        (item, position) = type.Decode(data, position, packed);
                    }

                    sequence.SetValue(item, i);
                }
                catch (Exception e) when (e is OverflowException or ArgumentException or IndexOutOfRangeException)
                {
                    throw new AbiException($"Failed to decode ABI sequence at element {i} for type {type}", e);
                }
            }

            return (sequence, Math.Max(position, dynamicPosition));
        }
    }
}
