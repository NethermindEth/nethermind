// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        private readonly LruCache<ValueKeccak, bool> _sealCache = new(2048, 2048, "ethash seals");
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

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            return ValidateExtraData(header)
                   && ValidateDifficulty(parent, header)
                   && (isUncle || ValidateTimestamp(header));


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool ValidateExtraData(BlockHeader blockHeader)
            {
                bool extraDataNotTooLong = blockHeader.ExtraData.Length <= 32;
                if (!extraDataNotTooLong && _logger.IsWarn)
                    _logger.Warn(
                        $"Invalid block header ({blockHeader.ToString(BlockHeader.Format.Full)}) - extra data too long {blockHeader.ExtraData.Length}");

                return extraDataNotTooLong;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool ValidateDifficulty(BlockHeader parentHeader, BlockHeader blockHeader)
            {
                UInt256 difficulty = _difficultyCalculator.Calculate(blockHeader, parentHeader);
                bool isDifficultyCorrect = difficulty == blockHeader.Difficulty;
                if (!isDifficultyCorrect && _logger.IsWarn)
                    _logger.Warn(
                        $"Invalid block header ({blockHeader.ToString(BlockHeader.Format.Full)}) - incorrect difficulty {blockHeader.Difficulty} instead of {difficulty}");

                return isDifficultyCorrect;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool ValidateTimestamp(BlockHeader blockHeader)
            {
                ulong unixTimeSeconds = _timestamper.UnixTime.Seconds;
                bool blockTooFarIntoFuture = blockHeader.Timestamp > unixTimeSeconds + AllowedFutureBlockTimeSeconds;
                if (blockTooFarIntoFuture && _logger.IsWarn)
                    _logger.Warn(
                        $"Invalid block header ({blockHeader.ToString(BlockHeader.Format.Full)}) - incorrect timestamp {blockHeader.Timestamp - unixTimeSeconds} seconds into the future");

                return !blockTooFarIntoFuture;
            }
        }
    }
}
