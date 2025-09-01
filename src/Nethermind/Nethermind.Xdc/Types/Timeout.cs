// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Consensus.HotStuff.Types;

using Round = ulong;

public class Timeout
{
    private Address signer;

    public Round Round { get; set; }
    public Signature Signature { get; set; }
    public ulong GapNumber { get; set; }

    public Hash256 Hash() => throw new NotImplementedException();

    public override string ToString() => $"{Round}:{GapNumber}";

    public Address GetSigner() => signer;
    public void SetSigner(Address signer) => this.signer = signer;
}
