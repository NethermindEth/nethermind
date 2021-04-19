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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class NewBlockHandler: IHandler<BlockRequestResult, NewBlockResult>
    {
        private const string AndWontBeAcceptedToTheTree = "and wont be accepted to the tree";
        private readonly IBlockTree _blockTree;
        private readonly IBlockPreprocessorStep _preprocessor;
        private readonly IBlockchainProcessor _processor;
        private readonly IStateProvider _stateProvider;
        private readonly ILogger _logger;
        
        public NewBlockHandler(IBlockTree blockTree, IBlockPreprocessorStep preprocessor, IBlockchainProcessor processor, IStateProvider stateProvider, ILogManager logManager)
        {
            _blockTree = blockTree;
            _preprocessor = preprocessor;
            _processor = processor;
            _stateProvider = stateProvider;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<NewBlockResult> Handle(BlockRequestResult request)
        {
            Block block = request.ToBlock();

            if (!ValidateRequestAndProcess(request, block, out Block? processedBlock) || processedBlock is null)
            {
                return ResultWrapper<NewBlockResult>.Success(new NewBlockResult {Valid = false});
            }

            AddBlockResult blockResult = _blockTree.SuggestBlock(processedBlock);
            bool isValid = blockResult is AddBlockResult.Added or AddBlockResult.AlreadyKnown;
            return ResultWrapper<NewBlockResult>.Success(new NewBlockResult {Valid = isValid});
        }

        private bool ValidateRequestAndProcess(BlockRequestResult request, Block block, out Block? processedBlock)
        {
            processedBlock = null;
            return CheckInput(request) && CheckParent(request.ParentHash, out BlockHeader? parent) && Process(block, parent!, out processedBlock);
        }

        private bool CheckParent(Keccak? parentHash, out BlockHeader? parent)
        {
            if (parentHash == null)
            {
                parent = null;
                return false;
            }
            else
            {
                parent = _blockTree.FindHeader(parentHash);
                return parent != null;
            }
        }

        private bool Process(Block block, BlockHeader parent, out Block? processedBlock)
        {
            block.Header.TotalDifficulty = parent.TotalDifficulty + block.Difficulty;
            
            _stateProvider.Reset();
            Keccak currentStateRoot = _stateProvider.StateRoot;
            try
            {
                _preprocessor.RecoverData(block);
                processedBlock = _processor.Process(block, ProcessingOptions.EthereumMerge, NullBlockTracer.Instance);
                if (processedBlock == null)
                {
                    if (_logger.IsWarn)
                    {
                        _logger.Warn($"Block {block.ToString(Block.Format.FullHashAndNumber)} cannot be processed {AndWontBeAcceptedToTheTree}.");
                    }

                    return false;
                }
            }
            finally
            {
                _stateProvider.Reset();
                _stateProvider.StateRoot = currentStateRoot;
                _stateProvider.RecalculateStateRoot();
            }

            return true;
        }

        private bool CheckInput(BlockRequestResult request) =>
            CheckInputIs(request, request.Difficulty, UInt256.One, nameof(request.Difficulty))
            && CheckInputIs(request, request.Nonce, 0ul, nameof(request.Nonce))
            && CheckInputIs<byte>(request, request.ExtraData, Array.Empty<byte>(), nameof(request.ExtraData))
            && CheckInputIs(request, request.MixHash, Keccak.Zero, nameof(request.MixHash))
            && CheckInputIs(request, request.Uncles, Array.Empty<Keccak>(), nameof(request.Uncles))
            && CheckInputIsNot(request, request.BlockHash, Keccak.Zero, nameof(request.BlockHash));

        private bool CheckInputIsNot<T>(BlockRequestResult request, T value, T expected, string name)
        {
            if (Equals(value, expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Block {request} has invalid {name}, expected not to be {expected}, got {value} {AndWontBeAcceptedToTheTree}.");
                return false;
            }

            return true;
        }

        private bool CheckInputIs<T>(BlockRequestResult request, T value, T expected, string name)
        {
            if (!Equals(value, expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Block {request} has invalid {name}, expected {expected}, got {value} {AndWontBeAcceptedToTheTree}.");
                return false;
            }

            return true;
        }
        
        private bool CheckInputIs<T>(BlockRequestResult request, IEnumerable<T>? value, IEnumerable<T> expected, string name)
        {
            if (!(value ?? Array.Empty<T>()).SequenceEqual(expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Block {request} has invalid {name}, expected {expected}, got {value} {AndWontBeAcceptedToTheTree}.");
                return false;
            }

            return true;
        }
    }
}
