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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.PoW.Test")]

namespace Nethermind.Consensus.Ethash
{
    internal class EthashSealValidator : ISealValidator
    {
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly IEthash _ethash;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        private readonly ICache<Keccak, bool> _sealCache = new LruCache<Keccak, bool>(2048, 2048, "ethash seals");
        private const int SealValidationIntervalConstantComponent = 1024;
        private const long AllowedFutureBlockTimeSeconds = 15;
        private int _sealValidationInterval = SealValidationIntervalConstantComponent;

        internal EthashSealValidator(ILogManager logManager, IDifficultyCalculator difficultyCalculator, ICryptoRandom cryptoRandom, IEthash ethash, ITimestamper timestamper)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _ethash = ethash ?? throw new ArgumentNullException(nameof(ethash));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            ResetValidationInterval();
        }

        private void ResetValidationInterval()
        {
            // more or less at the constant component
            // prevents attack on all Nethermind nodes at once
            _sealValidationInterval = SealValidationIntervalConstantComponent - 8 + _cryptoRandom.NextInt(16);
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            // genesis block is configured and assumed valid
            if (header.IsGenesis) return true;

            if (!force && header.Number % _sealValidationInterval != 0)
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
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - extra data too long {header.ExtraData.Length}");
                return false;
            }

            UInt256 difficulty = _difficultyCalculator.Calculate(header, parent);
            bool isDifficultyCorrect = difficulty == header.Difficulty;
            if (!isDifficultyCorrect)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - incorrect difficulty {header.Difficulty} instead of {difficulty}");
                return false;
            }

            ulong unixTimeSeconds = _timestamper.UnixTime.Seconds;
            bool blockTooFarIntoFuture = header.Timestamp > unixTimeSeconds + AllowedFutureBlockTimeSeconds;
            if (blockTooFarIntoFuture)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - incorrect timestamp {header.Timestamp - unixTimeSeconds} seconds into the future");
                return false;
            }

            return true;
        }
    }
}
