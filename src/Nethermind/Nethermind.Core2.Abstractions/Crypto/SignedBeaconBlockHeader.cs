// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Crypto
{
    public class SignedBeaconBlockHeader : IEquatable<SignedBeaconBlockHeader>
    {
        public static readonly SignedBeaconBlockHeader Zero =
            new SignedBeaconBlockHeader(BeaconBlockHeader.Zero, BlsSignature.Zero);

        public SignedBeaconBlockHeader(BeaconBlockHeader message, BlsSignature signature)
        {
            Message = message;
            Signature = signature;
        }

        public BeaconBlockHeader Message { get; }
        public BlsSignature Signature { get; }

        public bool Equals(SignedBeaconBlockHeader? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Message.Equals(other.Message) && Signature.Equals(other.Signature);
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj) || obj is SignedBeaconBlockHeader other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Message, Signature);
        }
    }
}
