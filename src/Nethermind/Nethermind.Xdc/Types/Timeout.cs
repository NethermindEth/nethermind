// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class Timeout(ulong round, Signature? signature, ulong gapNumber) : IXdcPoolItem
{
    private static readonly TimeoutDecoder _decoder = new();
    private Hash256 _hash;
    public ulong Round { get; set; } = round;
    public Signature? Signature { get; set; } = signature;
    public ulong GapNumber { get; set; } = gapNumber;
    public Address? Signer { get; set; }
    public Hash256 Hash => _hash ??= Keccak.Compute(_decoder.Encode(this, RlpBehaviors.None).Bytes);
    public override string ToString() => $"{Round}:{GapNumber}";
    public (ulong Round, Hash256 hash) PoolKey() => (Round, Keccak.Compute(_decoder.Encode(this, RlpBehaviors.ForSealing).Bytes));
}
