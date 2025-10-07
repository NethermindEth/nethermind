// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class Timeout(ulong round, Signature? signature, ulong gapNumber)
{
    private Address signer;

    public ulong Round { get; set; } = round;
    public Signature? Signature { get; set; } = signature;
    public ulong GapNumber { get; set; } = gapNumber;

    public override string ToString() => $"{Round}:{GapNumber}";

    public void SetSigner(Address signer) => this.signer = signer;
}
