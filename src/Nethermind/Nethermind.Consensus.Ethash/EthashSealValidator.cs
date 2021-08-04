//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Ethash
{
    public class EthashSealValidator : ISealValidator
    {
        private IDifficultyCalculator _difficultyCalculator;
        private ICryptoRandom _cryptoRandom;
        private IEthash _ethash;
        private ILogger _logger;

        private ICache<Keccak, bool> _sealCache = new LruCache<Keccak, bool>(2048, 2048, "ethash seals");
        private const int SealValidationIntervalConstantComponent = 1024;
        private int sealValidationInterval = SealValidationIntervalConstantComponent;

        public EthashSealValidator(ILogManager logManager, IDifficultyCalculator difficultyCalculator, ICryptoRandom cryptoRandom, IEthash ethash)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _ethash = ethash ?? throw new ArgumentNullException(nameof(ethash));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            ResetValidationInterval();
        }

        private void ResetValidationInterval()
        {
            // more or less at the constant component
            // prevents attack on all Nethermind nodes at once
            sealValidationInterval = SealValidationIntervalConstantComponent - 8 + _cryptoRandom.NextInt(16);
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            // genesis block is configured and assumed valid
            if (header.IsGenesis) return true;

            if (!force && header.Number % sealValidationInterval != 0)
            {
                return true;
            }

            // the cache will return false both if the seal was invalid and if it has never been checked before
            if (_sealCache.Get(header.Hash))
            {
                return true;
            }

            bool result = _ethash.Validate(header);
            _sealCache.Set(header.Hash, result);

            return result;
        }

        public void HintValidationRange(Guid guid, long start, long end)
        {
            _ethash.HintRange(guid, start, end);
        }  
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            if (!extraDataNotTooLong)
            {
                _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - extra data too long {header.ExtraData.Length}");
                return false;
            }

            UInt256 difficulty = _difficultyCalculator.Calculate(header, parent);
            bool isDifficultyCorrect = difficulty == header.Difficulty;
            if (!isDifficultyCorrect)
            {
                _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - incorrect difficulty {header.Difficulty} instead of {difficulty}");
                return false;
            }

            return true;
        }
    }
}
