//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Baseline.JsonRpc
{
    public class BaselineModule : IBaselineModule
    {
        private const int TruncationLength = 5;

        private readonly IAbiEncoder _abiEncoder;
        private readonly IFileSystem _fileSystem;
        private readonly IDb _baselineDb;
        private readonly ILogger _logger;
        private readonly ITxPoolBridge _txPoolBridge;

        private ConcurrentDictionary<Address, BaselineTree> _baselineTrees
            = new ConcurrentDictionary<Address, BaselineTree>();

        private BaselineMetadata _metadata;

        public BaselineModule(ITxPoolBridge txPoolBridge, IAbiEncoder abiEncoder, IFileSystem fileSystem, IDb baselineDb, ILogManager logManager)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _baselineDb = baselineDb ?? throw new ArgumentNullException(nameof(baselineDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));

            _metadata = LoadMetadata();
            InitTrees();
        }

        private void InitTrees()
        {
            foreach (Address trackedTree in _metadata.TrackedTrees)
            {
                TryAddTree(trackedTree);
            }
        }

        private byte[] _metadataKey = {0};

        private BaselineMetadata LoadMetadata()
        {
            byte[] serializedMetadata = _baselineDb[_metadataKey];
            BaselineMetadata metadata;
            if (serializedMetadata == null)
            {
                metadata = new BaselineMetadata();
            }
            else
            {
                RlpStream rlpStream = new RlpStream(serializedMetadata);
                metadata = new BaselineMetadata(rlpStream.DecodeArray(itemContext => itemContext.DecodeAddress()));
            }

            return metadata;
        }

        private bool TryAddTree(Address trackedTree)
        {
            ShaBaselineTree tree = new ShaBaselineTree(_baselineDb, trackedTree.Bytes, TruncationLength);
            return _baselineTrees.TryAdd(trackedTree, tree);
        }

        public Task<ResultWrapper<Keccak>> baseline_insertLeaf(Address address, Address contractAddress, Keccak hash)
        {
            if (hash == Keccak.Zero)
            {
                return Task.FromResult(ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput));
            }

            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeafAbiSig, hash);

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);
            return Task.FromResult(ResultWrapper<Keccak>.Success(txHash));
        }

        public Task<ResultWrapper<Keccak>> baseline_insertLeaves(Address address, Address contractAddress, params Keccak[] hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == Keccak.Zero)
                {
                    return Task.FromResult(ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput));
                }
            }

            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeavesAbiSig, new object[] {hashes});

            Transaction tx = new Transaction();

            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            return Task.FromResult(ResultWrapper<Keccak>.Success(txHash));
        }

        /// <summary>
        /// We retrieve the line 3 from here (bytecode) 
        /// 
        /// ======= MerkleTreeSHA.sol:MerkleTreeSHA =======
        /// Binary: 
        /// 608060405234801561001057600080fd5b5061080980610(...)
        /// </summary>
        /// <param name="contract"></param>
        /// <returns></returns>
        private async Task<byte[]> GetContractBytecode(string contract)
        {
            string[] contractBytecode = await _fileSystem.File.ReadAllLinesAsync($"contracts/{contract}.bin");
            if (contractBytecode.Length < 4)
            {
                throw new IOException("Bytecode not found");
            }

            if (_logger.IsInfo) _logger.Info($"Loading bytecode of {contractBytecode[1]}");
            return Bytes.FromHexString(contractBytecode[3]);
        }

        public async Task<ResultWrapper<Keccak>> baseline_deploy(Address address, string contractType)
        {
            byte[] bytecode;
            try
            {
                bytecode = await GetContractBytecode(contractType);
            }
            catch (IOException)
            {
                return ResultWrapper<Keccak>.Fail($"{contractType} bytecode could not be loaded.", ErrorCodes.ResourceNotFound);
            }

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Init = bytecode;
            tx.GasLimit = 2000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = address;

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");
            _logger.Info($"Contract {contractType} has been deployed");

            return ResultWrapper<Keccak>.Success(txHash);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getSiblings(Address contractAddress, long leafIndex)
        {
            if (leafIndex > MerkleTree.MaxLeafIndex || leafIndex < 0L)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail($"{leafIndex} is not a valid leaf index", ErrorCodes.InvalidInput));
            }

            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            if (!isTracked)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail($"{contractAddress} tree is not tracked", ErrorCodes.InvalidInput));
            }

            return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Success(tree!.GetProof((uint) leafIndex)));
        }

        public Task<ResultWrapper<bool>> baseline_track(Address contractAddress)
        {
            // can potentially warn user if tree is not deployed at the address

            if (TryAddTree(contractAddress))
            {
                UpdateMetadata(contractAddress);
                return Task.FromResult(ResultWrapper<bool>.Success(true));
            }

            return Task.FromResult(ResultWrapper<bool>.Fail($"{contractAddress} is already tracked", ErrorCodes.InvalidInput));
        }

        public Task<ResultWrapper<Address[]>> baseline_getTracked()
        {
            return Task.FromResult(ResultWrapper<Address[]>.Success(_metadata.TrackedTrees));
        }

        private void UpdateMetadata(Address contractAddress)
        {
            var list = _metadata.TrackedTrees.ToList();
            list.Add(contractAddress);
            _metadata.TrackedTrees = list.ToArray();

            _baselineDb[_metadataKey] = SerializeMetadata();
        }

        private byte[] SerializeMetadata()
        {
            int contentLength = 0;
            for (int i = 0; i < _metadata.TrackedTrees.Length; i++)
            {
                contentLength += Rlp.LengthOf(_metadata.TrackedTrees[i]);
            }

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new RlpStream(totalLength);
            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < _metadata.TrackedTrees.Length; i++)
            {
                rlpStream.Encode(_metadata.TrackedTrees[i]);
            }

            return rlpStream.Data;
        }
    }
}