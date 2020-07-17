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
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
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
        private readonly IStateReader _stateReader;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;

        private ConcurrentDictionary<Address, BaselineTree> _baselineTrees
            = new ConcurrentDictionary<Address, BaselineTree>();

        private BaselineMetadata _metadata;

        public BaselineModule(
            ITxPoolBridge txPoolBridge,
            IStateReader stateReader,
            ILogFinder logFinder,
            IBlockFinder blockFinder,
            IAbiEncoder abiEncoder,
            IFileSystem fileSystem,
            IDb baselineDb,
            ILogManager logManager)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _baselineDb = baselineDb ?? throw new ArgumentNullException(nameof(baselineDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));

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
                Address?[] addresses = rlpStream.DecodeArray(itemContext => itemContext.DecodeAddress());
                metadata = new BaselineMetadata(
                    addresses.Where(a => a != null).Select(a => a!).ToArray());
            }

            return metadata;
        }

        private bool TryAddTree(Address trackedTree)
        {
            if (_stateReader.GetCode(_blockFinder.Head.StateRoot, trackedTree).Length == 0)
            {
                return false;
            }
            
            ShaBaselineTree tree = new ShaBaselineTree(_baselineDb, trackedTree.Bytes, TruncationLength);
            return _baselineTrees.TryAdd(trackedTree, tree);
        }

        public Task<ResultWrapper<Keccak>> baseline_insertLeaf(Address address, Address contractAddress, Keccak hash)
        {
            if (hash == Keccak.Zero)
            {
                return Task.FromResult(ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput));
            }

            var txData = _abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                ContractMerkleTree.InsertLeafAbiSig,
                hash);

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

        public Task<ResultWrapper<Keccak>> baseline_insertLeaves(
            Address address,
            Address contractAddress,
            params Keccak[] hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == Keccak.Zero)
                {
                    var result = ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput);
                    return Task.FromResult(result);
                }
            }

            var txData = _abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                ContractMerkleTree.InsertLeavesAbiSig,
                new object[] {hashes});

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

        public Task<ResultWrapper<Keccak>> baseline_getRoot(Address contractAddress)
        {
            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);

            ResultWrapper<Keccak> result;
            if (!isTracked)
            {
                result = ResultWrapper<Keccak>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                // everything in memory
                tree = RebuildEntireTree(contractAddress);
                result = ResultWrapper<Keccak>.Success(tree.Root);
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode>> baseline_getLeaf(Address contractAddress, UInt256 leafIndex)
        {
            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            bool isLeafIndexValid = !(leafIndex > BaselineTree.MaxLeafIndex || leafIndex < 0L);

            ResultWrapper<BaselineTreeNode> result;
            if (!isLeafIndexValid)
            {
                result = ResultWrapper<BaselineTreeNode>.Fail(
                    $"{leafIndex} is not a valid leaf index",
                    ErrorCodes.InvalidInput);
            }
            else if (!isTracked)
            {
                result = ResultWrapper<BaselineTreeNode>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                // everything in memory
                tree = RebuildEntireTree(contractAddress);
                result = ResultWrapper<BaselineTreeNode>.Success(tree.GetLeaf((uint) leafIndex));
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getLeaves(
            Address contractAddress,
            params UInt256[] leafIndexes)
        {
            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            bool leafIndexesAreValid = true;
            foreach (UInt256 leafIndex in leafIndexes)
            {
                if (leafIndex > BaselineTree.MaxLeafIndex || leafIndex < 0L)
                {
                    leafIndexesAreValid = false;
                    break;
                }
            }

            ResultWrapper<BaselineTreeNode[]> result;
            if (!isTracked)
            {
                result = ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else if (!leafIndexesAreValid)
            {
                result = ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"one of the leaf indexes is not valid",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                // everything in memory
                tree = RebuildEntireTree(contractAddress);
                result = ResultWrapper<BaselineTreeNode[]>.Success(
                    tree.GetLeaves(leafIndexes.Select(i => (uint) i).ToArray()));
            }

            return Task.FromResult(result);
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
            ResultWrapper<Keccak> result;
            try
            {
                var bytecode = await GetContractBytecode(contractType);
                try
                {
                    Keccak txHash = DeployBytecode(address, contractType, bytecode);
                    result = ResultWrapper<Keccak>.Success(txHash);
                }
                catch (Exception e)
                {
                    result = ResultWrapper<Keccak>.Fail(
                        $"Provided bytecode could not be deployed. {e}",
                        ErrorCodes.InternalError);
                }
            }
            catch (IOException e)
            {
                result = ResultWrapper<Keccak>.Fail(
                    $"{contractType} bytecode could not be loaded. {e}",
                    ErrorCodes.ResourceNotFound);
            }
            catch (Exception e)
            {
                result = ResultWrapper<Keccak>.Fail(
                    $"{contractType} bytecode could not be loaded. {e}",
                    ErrorCodes.InternalError);
            }

            return result;
        }

        private static bool IsHex(string value)
        {
            if (value is null || value.Length % 2 != 0)
                return false;

            if (value.StartsWith("0x"))
            {
                value = value.Substring(2);
            }

            return value.All(
                c => (c >= '0' && c <= '9') ||
                     (c >= 'a' && c <= 'f') ||
                     (c >= 'A' && c <= 'F'));
        }

        public Task<ResultWrapper<Keccak>> baseline_deployBytecode(Address address, string byteCode)
        {
            ResultWrapper<Keccak> result;

            if (!IsHex(byteCode))
            {
                result = ResultWrapper<Keccak>.Fail("Provided bytecode could not be parsed.", ErrorCodes.InvalidInput);
            }
            else
            {
                var bytecodeBytes = Bytes.FromHexString(byteCode);
                try
                {
                    Keccak txHash = DeployBytecode(address, "bytecode", bytecodeBytes);
                    result = ResultWrapper<Keccak>.Success(txHash);
                }
                catch (Exception e)
                {
                    result = ResultWrapper<Keccak>.Fail(
                        $"Provided bytecode could not be deployed. {e}",
                        ErrorCodes.InternalError);
                }
            }

            return Task.FromResult(result);
        }

        private Keccak DeployBytecode(Address address, string contractType, byte[] bytecode)
        {
            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Init = bytecode;
            tx.GasLimit = 1000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = address;

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");
            _logger.Info($"Contract {contractType} has been deployed");
            return txHash;
        }

        private BaselineTree RebuildEntireTree(Address treeAddress)
        {
            // bad

            Keccak leavesTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
            LogFilter insertLeavesFilter = new LogFilter(
                0,
                new BlockParameter(0L),
                new BlockParameter(_blockFinder.Head.Number),
                new AddressFilter(treeAddress),
                new TopicsFilter(new SpecificTopic(leavesTopic)));

            Keccak leafTopic = new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0");
            LogFilter insertLeafFilter = new LogFilter(
                0,
                new BlockParameter(0L),
                new BlockParameter(_blockFinder.Head.Number),
                new AddressFilter(treeAddress),
                new TopicsFilter(new SpecificTopic(leafTopic))); // find tree topics

            var insertLeavesLogs = _logFinder.FindLogs(insertLeavesFilter);
            var insertLeafLogs = _logFinder.FindLogs(insertLeafFilter);
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), Array.Empty<byte>(), 5);

            // Keccak leafTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
            foreach (FilterLog filterLog in insertLeavesLogs)
            {
                for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                    baselineTree.Insert(leafHash);
                }
            }

            foreach (FilterLog filterLog in insertLeafLogs)
            {
                Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                baselineTree.Insert(leafHash);
            }

            return baselineTree;
        }

        public Task<ResultWrapper<bool>> baseline_verify(
            Address contractAddress,
            Keccak root,
            Keccak leaf,
            BaselineTreeNode[] path)
        {
            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            ResultWrapper<bool> result;
            if (!isTracked)
            {
                result = ResultWrapper<bool>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                // everything in memory
                tree = RebuildEntireTree(contractAddress);
                bool verificationResult = tree!.Verify(root, leaf, path);
                result = ResultWrapper<bool>.Success(verificationResult);
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getSiblings(Address contractAddress, long leafIndex)
        {
            if (leafIndex > BaselineTree.MaxLeafIndex || leafIndex < 0L)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"{leafIndex} is not a valid leaf index",
                    ErrorCodes.InvalidInput));
            }

            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out BaselineTree? tree);
            if (!isTracked)
            {
                var result = ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
                return Task.FromResult(result);
            }

            // everything in memory
            tree = RebuildEntireTree(contractAddress);

            return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Success(tree!.GetProof((uint) leafIndex)));
        }

        public Task<ResultWrapper<bool>> baseline_track(Address contractAddress)
        {
            ResultWrapper<bool> result;

            // can potentially warn user if tree is not deployed at the address
            if (TryAddTree(contractAddress))
            {
                UpdateMetadata(contractAddress);
                result = ResultWrapper<bool>.Success(true);
            }
            else
            {
                result = ResultWrapper<bool>.Fail(
                    $"{contractAddress} is already tracked or no contract at given address",
                    ErrorCodes.InvalidInput);    
            }

            return Task.FromResult(result);
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