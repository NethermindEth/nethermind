// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int SignedBeaconBlockHeaderLength = BeaconBlockHeaderLength + BlsSignatureLength;

        public static SignedBeaconBlockHeader DecodeSignedBeaconBlockHeader(ReadOnlySpan<byte> span)
        {
            if (span.Length != SignedBeaconBlockHeaderLength)
                ThrowSourceLength<SignedBeaconBlockHeader>(span.Length, SignedBeaconBlockHeaderLength);
            int offset = 0;
            return DecodeSignedBeaconBlockHeader(span, ref offset);
        }

        public static void Encode(Span<byte> span, SignedBeaconBlockHeader container)
        {
            if (span.Length != SignedBeaconBlockHeaderLength)
                ThrowTargetLength<SignedBeaconBlockHeader>(span.Length, SignedBeaconBlockHeaderLength);
            int offset = 0;
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedBeaconBlockHeader DecodeSignedBeaconBlockHeader(ReadOnlySpan<byte> span, ref int offset)
        {
            BeaconBlockHeader message = DecodeBeaconBlockHeader(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            return new SignedBeaconBlockHeader(message, signature);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedBeaconBlockHeader container, ref int offset)
        {
            Encode(span, container.Message, ref offset);
            Encode(span, container.Signature, ref offset);
        }
    }
}
