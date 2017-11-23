using System.Collections.Generic;
using System.Linq;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;
using Nevermind.Evm;
using Nevermind.Store;

namespace Nevermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly ChainId _chainId;
        private readonly ILogger _logger;
        private IStorageProvider _storageProvider;
        private IStateProvider _stateProvider;
        private IBlockProcessor _blockprocessor;
        private IBlockhashProvider _blockhashProvider;
        private TransactionProcessor _transactionProcessor;
        private IVirtualMachine _virtualMachine;

        public Block HeadBlock { get; private set; }

        private Dictionary<Keccak, Block> _chain = new Dictionary<Keccak, Block>();
        
        public void ProcessBlocks(List<Block> blocks)
        {
            // TODO: select by total difficulty
            // TODO: validate block
            // TODO: how to undo changes if forked?
            
            Dictionary<Keccak, Block> blocksByHash = blocks.ToDictionary(b => b.Hash, b => b);
            foreach (Block block in blocks.OrderBy(b => b.Header.Number))
            {
                blocksByHash.TryGetValue(block.Header.ParentHash, out Block parent);
                if (parent == null)
                {
                    _chain.TryGetValue(block.Header.ParentHash, out parent);
                    if (parent == null)
                    {
                        _logger?.Log($"DISCARDING BLOCK {block.Hash} (child of {block.Header.ParentHash}) {block.Header.Number}");
                    }
                }

                HeadBlock = _blockprocessor.ProcessBlock(parent, block.Header.Timestamp, block.Header.Beneficiary, block.Header.GasLimit, block.Header.ExtraData, block.Transactions, block.Ommers);
            }   
        }

        public Block GetBlock(Keccak hash)
        {
            _chain.TryGetValue(hash, out Block block);
            return block;
        }

        public BlockchainProcessor(IProtocolSpecification protocolSpecification, Block genesisBloc, ChainId chainId, ILogger logger)
        {
            HeadBlock = genesisBloc;
            _logger = logger;
            _protocolSpecification = protocolSpecification;
            _chainId = chainId;
            
            _storageProvider = new StorageProvider(logger);
            _stateProvider = new StateProvider(new StateTree(new InMemoryDb()), _protocolSpecification, logger);
            _blockhashProvider = new BlockhashProvider();
            _virtualMachine = new VirtualMachine(_protocolSpecification, _stateProvider, _storageProvider, _blockhashProvider, _logger);
            _transactionProcessor = new TransactionProcessor(_protocolSpecification, _stateProvider, _storageProvider, _virtualMachine, _chainId, _logger);
            _blockprocessor = new BlockProcessor(_protocolSpecification, _transactionProcessor, _stateProvider, _logger);
        }
    }
}