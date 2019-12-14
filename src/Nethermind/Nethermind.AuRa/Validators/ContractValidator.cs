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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public class ContractValidator : AuRaValidatorProcessorBase
    {
        internal static readonly Keccak PendingValidatorsKey = Keccak.Compute("PendingValidators");
        private static readonly PendingValidatorsDecoder _pendingValidatorsDecoder = new PendingValidatorsDecoder();
        private readonly ILogger _logger;
        private readonly IDb _stateDb;
        private readonly IStateProvider _stateProvider;
        private readonly ITransactionProcessor _transactionProcessor;
        
        private ValidatorContract _validatorContract;
        private PendingValidators _currentPendingValidators;
        private long _lastProcessedBlockNumber = 0;
        private IBlockFinalizationManager _blockFinalizationManager;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private Address[] _validators;
        private bool _isProducing;

        protected Address ContractAddress { get; }
        protected IAbiEncoder AbiEncoder { get; }
        protected long InitBlockNumber { get; }
        protected CallOutputTracer Output { get; } = new CallOutputTracer();
        protected ValidatorContract ValidatorContract => _validatorContract ??= CreateValidatorContract(ContractAddress);

        protected override Address[] Validators
        {
            get => _validators ??= LoadValidatorsFromContract(_blockTree.Head);
            set => _validators = value;
        }
        
        private PendingValidators CurrentPendingValidators => _currentPendingValidators;

        public ContractValidator(
            AuRaParameters.Validator validator,
            IDb stateDb,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            long startBlockNumber) : base(validator, logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            ContractAddress = validator.Addresses?.FirstOrDefault() ?? throw new ArgumentException("Missing contract address for AuRa validator.", nameof(validator.Addresses));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            InitBlockNumber = startBlockNumber;
            SetPendingValidators(LoadPendingValidators());
        }

        protected override void SetFinalizationManagerInternal(IBlockFinalizationManager finalizationManager, in bool forProducing)
        {
            base.SetFinalizationManagerInternal(finalizationManager, in forProducing);
            
            if (_blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }

            _blockFinalizationManager = finalizationManager;
            _isProducing = forProducing;
            
            if (!forProducing && _blockFinalizationManager != null)
            {
                _blockFinalizationManager.BlocksFinalized += OnBlocksFinalized;
            }
        }

        public override void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            var isProducingBlock = options.IsProducingBlock();
            var isProcessingBlock = !isProducingBlock;
            
            if (_validators == null || isProducingBlock)
            {
                Validators = LoadValidatorsFromContract(block.Header);
            }

            if (InitBlockNumber == block.Number)
            {
                InitiateChange(block, Validators.ToArray(), isProcessingBlock, true);
            }
            else
            {
                if (isProcessingBlock)
                {
                    bool reorganisationHappened = block.Number <= _lastProcessedBlockNumber;
                    if (reorganisationHappened)
                    {
                        var reorganisationToBlockBeforePendingValidatorsInitChange = block.Number <= CurrentPendingValidators?.BlockNumber;
                        var pendingValidators = reorganisationToBlockBeforePendingValidatorsInitChange ? null : LoadPendingValidators();
                        SetPendingValidators(pendingValidators);
                    }
                    else if (block.Number > _lastProcessedBlockNumber + 1) // blocks skipped, like fast sync
                    {
                        SetPendingValidators(TryGetInitChangeFromPastBlocks(block.ParentHash), true);
                    }
                }
                else
                {
                    // if we are not processing blocks we are not on consecutive blocks.
                    // We need to initialize pending validators from db on each block being produced.  
                    SetPendingValidators(LoadPendingValidators());
                }
            }
            
            base.PreProcess(block, options);
            
            FinalizePendingValidatorsIfNeeded(block.Header, isProcessingBlock);
            
            _lastProcessedBlockNumber = block.Number;
        }

        private PendingValidators TryGetInitChangeFromPastBlocks(Keccak blockHash)
        {
            PendingValidators pendingValidators = null;
            var lastFinalized = _blockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
            var toBlock = Math.Max(lastFinalized, InitBlockNumber);
            var block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
            while (block?.Number >= toBlock)
            {
                var receipts = block.Transactions.Select(t => _receiptStorage.Find(t.Hash)).Where(r => r != null).ToArray();
                if (ValidatorContract.CheckInitiateChangeEvent(ContractAddress, block.Header, receipts, out var potentialValidators))
                {
                    if (Validators.SequenceEqual(potentialValidators))
                    {
                        break;
                    }

                    pendingValidators = new PendingValidators(block.Number, block.Hash, potentialValidators);
                }
                block = _blockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.None);
            }

            return pendingValidators;
        }

        public override void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            base.PostProcess(block, receipts, options);
            
            if (ValidatorContract.CheckInitiateChangeEvent(ContractAddress, block.Header, receipts, out var potentialValidators))
            {
                var isProcessingBlock = !options.IsProducingBlock();
                InitiateChange(block, potentialValidators, isProcessingBlock, Validators.Length == 1);
                if (_logger.IsInfo && isProcessingBlock) _logger.Info($"Signal for transition within contract at block {block.ToString(Block.Format.Short)}. New list of {potentialValidators.Length} : [{string.Join<Address>(", ", potentialValidators)}].");
            }
        }

        private void FinalizePendingValidatorsIfNeeded(BlockHeader block, bool isProcessingBlock)
        {
            if (CurrentPendingValidators?.AreFinalized == true)
            {
                if (_logger.IsInfo && isProcessingBlock) _logger.Info($"Applying validator set change signalled at block {CurrentPendingValidators.BlockNumber} before block {block.ToString(BlockHeader.Format.Short)}.");
                if (block.Number == InitBlockNumber)
                {
                    ValidatorContract.TryInvokeTransaction(block, _transactionProcessor, ValidatorContract.FinalizeChange(), Output);
                }
                else
                {
                    ValidatorContract.InvokeTransaction(block, _transactionProcessor, ValidatorContract.FinalizeChange(), Output);
                }
                SetPendingValidators(null, isProcessingBlock);
            }
        }
        
        protected virtual ValidatorContract CreateValidatorContract(Address contractAddress) => new ValidatorContract(AbiEncoder, contractAddress);
        
        private void InitiateChange(Block block, Address[] potentialValidators, bool isProcessingBlock, bool initiateChangeIsImmediatelyFinalized = false)
        {
            // We are ignoring the signal if there are already pending validators. This replicates Parity behaviour which can be seen as a bug.
            if (CurrentPendingValidators == null && potentialValidators.Length > 0)
            {
                SetPendingValidators(new PendingValidators(block.Number, block.Hash, potentialValidators)
                    {
                        AreFinalized = initiateChangeIsImmediatelyFinalized
                    },
                    !initiateChangeIsImmediatelyFinalized && isProcessingBlock);
            }
        }

        private Address[] LoadValidatorsFromContract(BlockHeader blockHeader)
        {
            ValidatorContract.EnsureSystemAccount(_stateProvider);
            ValidatorContract.InvokeTransaction(blockHeader, _transactionProcessor, ValidatorContract.GetValidators(), Output);

            if (Output.ReturnValue.Length == 0)
            {
                throw new AuRaException("Failed to initialize validators list.");
            }
            
            var validators = ValidatorContract.DecodeAddresses(Output.ReturnValue);
            if (validators.Length == 0)
            {
                throw new AuRaException("Failed to initialize validators list.");
            }
            
            if(_logger.IsInfo && !_isProducing) _logger.Info($"Initial contract {validators.Length} validators: [{string.Join<Address>(", ", validators)}].");
           
            return validators;
        }

        private void OnBlocksFinalized(object sender, FinalizeEventArgs e)
        {
            if (CurrentPendingValidators != null)
            {
                // .Any equivalent with for
                var currentPendingValidatorsBlockGotFinalized = false;
                for (int i = 0; i < e.FinalizedBlocks.Count && !currentPendingValidatorsBlockGotFinalized; i++)
                {
                    currentPendingValidatorsBlockGotFinalized = e.FinalizedBlocks[i].Hash == CurrentPendingValidators.BlockHash;
                }
                
                if (currentPendingValidatorsBlockGotFinalized)
                {
                    CurrentPendingValidators.AreFinalized = true;
                    Validators = CurrentPendingValidators.Addresses;
                    SetPendingValidators(CurrentPendingValidators, true);
                    if (_logger.IsInfo && !_isProducing) _logger.Info($"Finalizing validators for transition within contract signalled at block {CurrentPendingValidators.BlockNumber}. after block {e.FinalizingBlock.ToString(BlockHeader.Format.Short)}.");
                }
            }
        }

        private PendingValidators LoadPendingValidators()
        {
            var rlpStream = new RlpStream(_stateDb.Get(PendingValidatorsKey) ?? Rlp.OfEmptySequence.Bytes);
            return _pendingValidatorsDecoder.Decode(rlpStream);
        }

        private void SetPendingValidators(PendingValidators validators, bool canSave = false)
        {
            _currentPendingValidators = validators;
            
            // We don't want to save to db when:
            // * We are producing block
            // * We will save later on processing same block (stateDb ignores consecutive calls with same key!)
            // * We are loading validators from db.
            if (canSave)
            {
                _stateDb.Set(PendingValidatorsKey, _pendingValidatorsDecoder.Encode(CurrentPendingValidators).Bytes);
            }
        }

        internal class PendingValidators
        {
            public PendingValidators(long blockNumber, Keccak blockHash, Address[] addresses)
            {
                BlockNumber = blockNumber;
                BlockHash = blockHash;
                Addresses = addresses;
            }

            public Address[] Addresses { get; }
            public long BlockNumber { get; }
            public Keccak BlockHash { get; }
            public bool AreFinalized { get; set; }
        }

        private class PendingValidatorsDecoder : IRlpDecoder<PendingValidators>
        {
            static PendingValidatorsDecoder()
            {
                Rlp.Decoders[typeof(PendingValidators)] = new PendingValidatorsDecoder();
            }
            
            public PendingValidators Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                if (rlpStream.IsNextItemNull())
                {
                    rlpStream.ReadByte();
                    return null;
                }
                
                var sequenceLength = rlpStream.ReadSequenceLength();
                var pendingValidatorsCheck = rlpStream.Position + sequenceLength;

                var blockNumber = rlpStream.DecodeLong();
                var blockHash = rlpStream.DecodeKeccak();
                
                var addressSequenceLength = rlpStream.ReadSequenceLength();
                var addressCheck = rlpStream.Position + addressSequenceLength;
                List<Address> addresses = new List<Address>();
                while (rlpStream.Position < addressCheck)
                {
                    addresses.Add(rlpStream.DecodeAddress());
                }
                rlpStream.Check(addressCheck);
                
                var result = new PendingValidators(blockNumber, blockHash, addresses.ToArray())
                {
                    AreFinalized = rlpStream.DecodeBool()
                };
                
                rlpStream.Check(pendingValidatorsCheck);
                
                return result;
            }

            public Rlp Encode(PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                if (item == null)
                {
                    return Rlp.OfEmptySequence;
                }
            
                RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
                Encode(rlpStream, item, rlpBehaviors);
                return new Rlp(rlpStream.Data);
            }

            public void Encode(MemoryStream stream, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
                Rlp.StartSequence(stream, contentLength);
                Rlp.Encode(stream, item.BlockNumber);
                Rlp.Encode(stream, item.BlockHash);
                Rlp.StartSequence(stream, addressesLength);
                for (int i = 0; i < item.Addresses.Length; i++)
                {
                    Rlp.Encode(stream, item.Addresses[i]);
                }
                Rlp.Encode(stream, item.AreFinalized);
            }
            
            public void Encode(RlpStream rlpStream, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(item.BlockNumber);
                rlpStream.Encode(item.BlockHash);
                rlpStream.StartSequence(addressesLength);
                for (int i = 0; i < item.Addresses.Length; i++)
                {
                    rlpStream.Encode(item.Addresses[i]);
                }
                rlpStream.Encode(item.AreFinalized);
            }

            public int GetLength(PendingValidators item, RlpBehaviors rlpBehaviors) =>
                item == null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

            private (int Total, int Addresses) GetContentLength(PendingValidators item, RlpBehaviors rlpBehaviors)
            {
                int contentLength = Rlp.LengthOf(item.BlockNumber) 
                                    + Rlp.LengthOf(item.BlockHash) 
                                    + Rlp.LengthOf(item.AreFinalized); 
                
                var addressesLength = GetAddressesLength(item.Addresses);
                contentLength += Rlp.LengthOfSequence(addressesLength);
                
                return (contentLength, addressesLength);
            }

            private int GetAddressesLength(Address[] addresses) => addresses.Sum(Rlp.LengthOf);
        }
    }
}