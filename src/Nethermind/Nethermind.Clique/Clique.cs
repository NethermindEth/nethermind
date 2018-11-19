/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Clique
{
    [Todo(Improve.Documentation, "Add description of each parameter")]
    public static class Clique
    {
        public const int CheckpointInterval = 1024;
        public const int DefaultEpochLength = 30000;

        public const int InMemorySnapshots = 128;
        public const int InMemorySignatures = 4096;

        public const int WiggleTime = 500;

        public const int ExtraVanityLength = 32;
        public const int ExtraSealLength = 65;

        public const ulong NonceAuthVote = ulong.MaxValue;
        public const ulong NonceDropVote = 0UL;

        public static UInt256 DifficultyInTurn = 2;
        public static UInt256 DifficultyNoTurn = UInt256.One;
    }
}