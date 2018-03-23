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

using System.Numerics;

namespace Nethermind.Core.Specs
{
    public class DynamicReleaseSpec : IReleaseSpec
    {
        private readonly ISpecProvider _specProvider;
        private IReleaseSpec _currentRelease;
        private BigInteger _currentBlockNumber;

        public DynamicReleaseSpec(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
            _currentRelease = specProvider.GetSpec(BigInteger.Zero);
        }

        public BigInteger CurrentBlockNumber
        {
            get => _currentBlockNumber;
            set
            {
                _currentBlockNumber = value;
                _currentRelease = _specProvider.GetSpec(_currentBlockNumber);
            }
        }

        public bool IsTimeAdjustmentPostOlympic => _currentRelease.IsTimeAdjustmentPostOlympic;
        public bool AreJumpDestinationsUsed => _currentRelease.AreJumpDestinationsUsed;
        public bool IsEip2Enabled => _currentRelease.IsEip2Enabled;
        public bool IsEip7Enabled => _currentRelease.IsEip7Enabled;
        public bool IsEip100Enabled => _currentRelease.IsEip100Enabled;
        public bool IsEip140Enabled => _currentRelease.IsEip140Enabled;
        public bool IsEip150Enabled => _currentRelease.IsEip150Enabled;
        public bool IsEip155Enabled => _currentRelease.IsEip155Enabled;
        public bool IsEip158Enabled => _currentRelease.IsEip158Enabled;
        public bool IsEip160Enabled => _currentRelease.IsEip160Enabled;
        public bool IsEip170Enabled => _currentRelease.IsEip170Enabled;
        public bool IsEip196Enabled => _currentRelease.IsEip196Enabled;
        public bool IsEip197Enabled => _currentRelease.IsEip197Enabled;
        public bool IsEip198Enabled => _currentRelease.IsEip198Enabled;
        public bool IsEip211Enabled => _currentRelease.IsEip211Enabled;
        public bool IsEip214Enabled => _currentRelease.IsEip214Enabled;
        public bool IsEip649Enabled => _currentRelease.IsEip649Enabled;
        public bool IsEip658Enabled => _currentRelease.IsEip658Enabled;
    }
}