using System;
using Nethermind.Core;
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
            // TODO: all until we properly optimize ethash, still with sensible security measures (although there are many attack vectors for this particular node during sync)
            if (header.Number < 750000)
            {
                return true;
            }

            if (header.Number < 6500000 && header.Number % 30000 != 0) // TODO: this numbers are here to secure mainnet only (current block and epoch length) 
            {
                return true;
            }
            
            return _ethash.Validate(header);
        }
        
        public bool ValidateParams(Block parent, BlockHeader header)
        {   
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            if (!extraDataNotTooLong)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - extra data too long {header.ExtraData.Length}");
                return false;
            }
            
            UInt256 difficulty = _difficultyCalculator.Calculate(parent.Header.Difficulty, parent.Header.Timestamp, header.Timestamp, header.Number, parent.Ommers.Length > 0);
            bool isDifficultyCorrect = difficulty == header.Difficulty;
            if (!isDifficultyCorrect)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - incorrect diffuclty {header.Difficulty} instead of {difficulty}");
                return false;
            }

            return true;
        }   
    }
}