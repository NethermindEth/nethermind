using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining.Difficulty;

namespace Nethermind.Mining
{
    public class EthashSealValidator : ISealValidator
    {
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly IEthash _ethash;
        private ILogger _logger;
        
        public EthashSealValidator(ILogManager logManager, IDifficultyCalculator difficultyCalculator, IEthash ethash)
        {
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            _ethash = ethash ?? throw new ArgumentNullException(nameof(ethash));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public bool ValidateSeal(BlockHeader header)
        {
            if (header.Number % 64 != 0 || header.Number == 0)
            {
                return true;
            }

            return _ethash.Validate(header);
        }
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {   
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            if (!extraDataNotTooLong)
            {
                _logger.Warn($"Invalid block header ({header.ToString(BlockHeader.Format.Full)}) - extra data too long {header.ExtraData.Length}");
                return false;
            }
            
            UInt256 difficulty = _difficultyCalculator.Calculate(parent.Difficulty, parent.Timestamp, header.Timestamp, header.Number, parent.OmmersHash != Keccak.OfAnEmptySequenceRlp);
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