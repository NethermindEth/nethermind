// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.Types;

using Round = ulong;

public class Timeout(ulong round, Signature signature, ulong gapNumber)
{
    private Address signer;

    public Round Round { get; set; } = round;
    public Signature Signature { get; set; } = signature;
    public ulong GapNumber { get; set; } = gapNumber;

    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);

    public override string ToString() => $"{Round}:{GapNumber}";

    public Address GetSigner() => signer;
    public void SetSigner(Address signer) => this.signer = signer;
}
