// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Crypto
{
    public class SignedBeaconBlock
    {
        public SignedBeaconBlock(BeaconBlock message, BlsSignature signature)
        {
            Message = message;
            Signature = signature;
        }

        public BeaconBlock Message { get; }
        public BlsSignature Signature { get; }
    }
}
