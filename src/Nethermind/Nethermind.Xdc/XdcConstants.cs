// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal static class XdcConstants
{
    public const ulong EpochLength = 900UL; // Default number of blocks after which to checkpoint and reset the pending votes

    public const int ExtraVanity = 32; // Fixed number of extra-data prefix bytes reserved for signer vanity
    public const int ExtraSeal = 65;   // Fixed number of extra-data suffix bytes reserved for signer seal

    public const ulong NonceAuthVoteValue = 0xffffffffffffffff; // Magic nonce number to vote on adding a new signer
    public const ulong NonceDropVoteValue = 0x0000000000000000; // Magic nonce number to vote on removing a signer

    public const int InMemoryEpochs = 5 * 900;   // Number of mapping from block to epoch switch infos to keep in memory

    public const int InMemoryRound2Epochs = 65536;   // One epoch ~ 0.5h, 65536 epochs ~ 3.7y, ~10MB memory

    public const byte ConsensusVersion = 0x02;

    // --- Compile-time constants ---
    public const int InMemorySnapshots = 128;       // Number of recent vote snapshots to keep in memory
    public const int BlockSignersCacheLimit = 9000;
    public const int M2ByteLength = 4;

    public const int PeriodicJobPeriod = 60;
    public const int PoolHygieneRound = 10;
    public const int InMemorySignatures = 4096;

    public static readonly Hash256 UncleHash = Keccak.OfAnEmptySequenceRlp; // Always Keccak256(RLP([])) as uncles are meaningless outside of PoW
    public static readonly UInt256 DifficultyDefault = UInt256.One;
}
