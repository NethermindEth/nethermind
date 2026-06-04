// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Clique
{
    /// <summary>
    /// This small thing caused so much trouble that it deserves its own class
    /// </summary>
    internal class WiggleRandomizer(ICryptoRandom cryptoRandom, ISnapshotManager snapshotManager, int minWiggle = 0)
    {
        private readonly ICryptoRandom _cryptoRandom = cryptoRandom;
        private readonly ISnapshotManager _snapshotManager = snapshotManager;
        private readonly int _minimumWiggle = minWiggle;
        private long _lastWiggleAtNumber;

        private int _lastWiggle;

        public int WiggleFor(BlockHeader header)
        {
            if (header.Difficulty == Clique.DifficultyInTurn)
            {
                return 0;
            }

            if (header.Number != _lastWiggleAtNumber)
            {
                int multiplier = _snapshotManager.GetOrCreateSnapshot(header.Number - 1, header.ParentHash!).Signers.Count / 2 + 1;
                int randomPart = _cryptoRandom.NextInt(multiplier * Clique.WiggleTime);
                _lastWiggle = randomPart < _minimumWiggle ? _minimumWiggle : randomPart;
                _lastWiggleAtNumber = header.Number;
            }

            return _lastWiggle;
        }
    }
}
