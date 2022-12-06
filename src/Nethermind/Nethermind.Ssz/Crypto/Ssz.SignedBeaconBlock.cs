// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        private const int SignedBeaconBlockDynamicOffset = VarOffsetSize + BlsSignatureLength;

        public static int SignedBeaconBlockLength(SignedBeaconBlock container)
        {
            return SignedBeaconBlockDynamicOffset + Ssz.BeaconBlockLength(container.Message);
        }

        public static SignedBeaconBlock DecodeSignedBeaconBlock(ReadOnlySpan<byte> span)
        {
            // fixed parts
            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int messageDynamicOffset);
            BlsSignature signature = DecodeBlsSignature(span.Slice(offset, BlsSignatureLength));
            offset += BlsSignatureLength;

            // variable parts
            BeaconBlock message = DecodeBeaconBlock(span.Slice(messageDynamicOffset));

            return new SignedBeaconBlock(message, signature);
        }

        public static void Encode(Span<byte> span, SignedBeaconBlock container)
        {
            int expectedLength = SignedBeaconBlockLength(container);
            if (span.Length != expectedLength)
                ThrowTargetLength<SignedBeaconBlock>(span.Length, expectedLength);
            int offset = 0;
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedBeaconBlock container, ref int offset)
        {
            // Semantics of Encode = write container into span at offset, then increase offset by the bytes written

            // Static
            Encode(span, Ssz.SignedBeaconBlockDynamicOffset, ref offset);
            Encode(span, container.Signature, ref offset);

            // Variable
            Encode(span, container.Message, ref offset);
        }
    }
}
