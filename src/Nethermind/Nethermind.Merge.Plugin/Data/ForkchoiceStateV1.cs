// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Arguments to engine_ForkChoiceUpdate
///
/// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#forkchoicestatev1"/>
/// </summary>
public class ForkchoiceStateV1
{
    public ForkchoiceStateV1(Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash)
    {
        HeadBlockHash = headBlockHash;
        FinalizedBlockHash = finalizedBlockHash;
        SafeBlockHash = safeBlockHash;
    }

    /// <summary>
    /// Hash of the head of the canonical chain.
    /// </summary>
    public Keccak HeadBlockHash { get; set; }

    /// <summary>
    /// Safe block hash of the canonical chain under certain synchrony and honesty assumptions. This value MUST be either equal to or an ancestor of headBlockHash.
    /// </summary>
    /// <remarks>Can be <see cref="Keccak.Zero"/> when transition block is not finalized yet.</remarks>
    public Keccak SafeBlockHash { get; set; }

    /// <summary>
    /// Hash of the most recent finalized block
    /// </summary>
    /// <remarks>Can be <see cref="Keccak.Zero"/> when transition block is not finalized yet.</remarks>
    public Keccak FinalizedBlockHash { get; set; }

    public override string ToString() => $"ForkChoice: Head: {HeadBlockHash.ToShortString()}, Safe: {SafeBlockHash.ToShortString()}, Finalized: {FinalizedBlockHash.ToShortString()}";
    public string ToString(long? headNumber, long? safeNumber, long? finalizedNumber) =>
        headNumber is null || safeNumber is null || finalizedNumber is null
            ? ToString()
            : $"ForkChoice: Head: {headNumber} ({HeadBlockHash.ToShortString()}), Safe: {safeNumber} ({SafeBlockHash.ToShortString()}), Finalized: {finalizedNumber} ({FinalizedBlockHash.ToShortString()})";
}
