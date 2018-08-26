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

using System.Collections.Generic;
using System.Numerics;
using System.Security;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.KeyStore;
using Nethermind.Store;

namespace Nethermind.JsonRpc.Module
{
    public class BlockchainBridge : IBlockchainBridge
    {
        private readonly IEthereumSigner _signer;
        private readonly IBlockTree _blockTree;
        private readonly ITransactionStore _transactionStore;
        private readonly IDb _db;
        private readonly IStateProvider _stateProvider;
        private readonly IKeyStore _keyStore;

        public BlockchainBridge(IEthereumSigner signer, IStateProvider stateProvider, IKeyStore keyStore, IBlockTree blockTree, IDb db, ITransactionStore transactionStore)
        {
            _signer = signer;
            _stateProvider = stateProvider;
            _keyStore = keyStore;
            _blockTree = blockTree;
            _db = db;
            _transactionStore = transactionStore;
        }

        public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses()
        {
            return _keyStore.GetKeyAddresses();
        }

        public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            return _keyStore.GetKey(address, password);
        }

        public BlockHeader Head => _blockTree.Head;
        public BlockHeader BestSuggested => _blockTree.BestSuggested;
        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            return _blockTree.FindBlock(blockHash, mainChainOnly);
        }

        public Block FindBlock(UInt256 blockNumber)
        {
            return _blockTree.FindBlock(blockNumber);
        }

        public Block RetrieveHeadBlock()
        {
            return _blockTree.FindBlock(_blockTree.Head.Hash, false);
        }

        public Block RetrieveGenesisBlock()
        {
            return _blockTree.FindBlock(_blockTree.Genesis.Hash, true);
        }

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            return _signer.Sign(privateKey, message);
        }

        public Transaction GetTransaction(Keccak transactionHash)
        {
            return _transactionStore.GetTransaction(transactionHash);
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _transactionStore.GetBlockHash(transactionHash);
        }

        public TransactionReceipt GetTransactionReceipt(Keccak transactionHash)
        {
            return _transactionStore.GetTransactionReceipt(transactionHash);
        }

        public byte[] GetCode(Address address)
        {
            return _stateProvider.GetCode(address);
        }

        public byte[] GetCode(Keccak codeHash)
        {
            return _stateProvider.GetCode(codeHash);
        }

        public BigInteger GetNonce(Address address)
        {
            return _stateProvider.GetNonce(address);
        }

        public BigInteger GetBalance(Address address)
        {
            return _stateProvider.GetBalance(address);
        }

        public Account GetAccount(Address address, Keccak stateRoot)
        {
            var stateTree = new StateTree(_db, stateRoot);
            return stateTree.Get(address);
        }
    }
}