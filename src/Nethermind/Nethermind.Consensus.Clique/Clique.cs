// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Consensus.Clique
{
    internal static class Clique
    {
        /// <summary>
        /// Number of blocks between the checkpoints
        /// </summary>
        public const int CheckpointInterval = 1024;

        /// <summary>
        /// Number of blocks within the Clique epoch
        /// </summary>
        public const int DefaultEpochLength = 30000;

        /// <summary>
        /// Snapshots cache size
        /// </summary>
        public const int InMemorySnapshots = 128;

        /// <summary>
        /// Signatures cache size
        /// </summary>
        public const int InMemorySignatures = 4096;

        /// <summary>
        /// Delay time before producing out-of-turn block
        /// </summary>
        public const int WiggleTime = 500;

        /// <summary>
        /// Length of extra vanity within the extra data
        /// </summary>
        public const int ExtraVanityLength = 32;

        /// <summary>
        /// Length of an extra seal within the extra data
        /// </summary>
        public const int ExtraSealLength = 65;

        /// <summary>
        /// Nonce to set on the block header when adding a vote
        /// </summary>
        public const ulong NonceAuthVote = ulong.MaxValue;

        /// <summary>
        /// Nonce to set on the block header when removing a previous signer vote
        /// </summary>
        public const ulong NonceDropVote = 0UL;

        /// <summary>
        /// Difficulty of a block produced by a signer in turn
        /// </summary>
        public static UInt256 DifficultyInTurn = 2;

        /// <summary>
        /// Difficulty of a block produced by an alternative signer (out of turn)
        /// </summary>
        public static UInt256 DifficultyNoTurn = UInt256.One;
    }
}
