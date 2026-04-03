// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class Timeout(ulong round, Signature? signature, ulong gapNumber, bool isMyVote = false) : RlpHashEqualityBase, IXdcPoolItem
{
    private static readonly TimeoutDecoder _timeoutDecoder = new();
    public ulong Round { get; set; } = round;
    public Signature? Signature { get; set; } = signature;
    public ulong GapNumber { get; set; } = gapNumber;
    public Address? Signer { get; set; }
    public bool IsMyVote { get; } = isMyVote;
    public override string ToString() => $"{Round}:{GapNumber}";
    public (ulong Round, Hash256 hash) PoolKey() => (Round, Keccak.Compute(_timeoutDecoder.Encode(this, RlpBehaviors.ForSealing).Bytes));
    protected override void Encode(KeccakRlpStream stream) =>
        _timeoutDecoder.Encode(stream, this, RlpBehaviors.None);
}
