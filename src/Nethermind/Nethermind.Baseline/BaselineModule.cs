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
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;

namespace Nethermind.Baseline
{
    public class BaselineModule : IBaselineModule
    {
        public const int TruncationLength = 5;
        public static Keccak LeafTopic = new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0");
        public static Keccak LeavesTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
        private const TxHandlingOptions TxHandlingOptions = TxPool.TxHandlingOptions.ManagedNonce | TxPool.TxHandlingOptions.PersistentBroadcast;

        public BaselineModule(
            ITxSender txSender,
            IStateReader stateReader,
            ILogFinder logFinder,
            IBlockFinder blockFinder,
            IAbiEncoder abiEncoder,
            IFileSystem fileSystem,
            IDb baselineDb,
            IDb metadataBaselineDb,
            ILogManager logManager,
            IBlockProcessor blockProcessor,
            DisposableStack disposableStack)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _baselineDb = baselineDb ?? throw new ArgumentNullException(nameof(baselineDb));
            _metadataBaselineDb = metadataBaselineDb ?? throw new ArgumentNullException(nameof(metadataBaselineDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _baselineTreeHelper = new BaselineTreeHelper(_logFinder, baselineDb, metadataBaselineDb, _logger);
            _disposableStack = disposableStack ?? throw new ArgumentNullException(nameof(disposableStack));

            _metadata = LoadMetadata();
            InitTrees();
        }

        public async Task<ResultWrapper<Keccak>> baseline_insertCommit(Address address, Address contractAddress, Keccak hash)
        {
            if (hash == Keccak.Zero)
            {
                return ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput);
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
            tx.GasPrice = 20.GWei();

            Keccak txHash = (await _txSender.SendTransaction(tx, TxHandlingOptions)).Hash;
            return ResultWrapper<Keccak>.Success(txHash);
        }

        public async Task<ResultWrapper<Keccak>> baseline_insertCommits(
            Address address,
            Address contractAddress,
            params Keccak[] hashes)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == Keccak.Zero)
                {
                    var result = ResultWrapper<Keccak>.Fail("Cannot insert zero hash", ErrorCodes.InvalidInput);
                    return result;
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
            tx.GasPrice = 20.GWei();

            Keccak txHash = (await _txSender.SendTransaction(tx, TxHandlingOptions)).Hash;

            return ResultWrapper<Keccak>.Success(txHash);
        }

        public Task<ResultWrapper<Keccak>> baseline_getRoot(
            Address contractAddress,
            BlockParameter? blockParameter = null)
        {
            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);

            ResultWrapper<Keccak> result;
            if (!isTracked || tree == null)
            {
                result = ResultWrapper<Keccak>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                if (blockParameter == null || blockParameter == BlockParameter.Latest)
                {
                    result = ResultWrapper<Keccak>.Success(tree.Root);
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<Keccak>.Fail(searchResult);
                    }
                    else
                    {
                        var historicalTree = _baselineTreeHelper.CreateHistoricalTree(contractAddress, searchResult.Object.Number);
                        result = ResultWrapper<Keccak>.Success(historicalTree.Root);
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode>> baseline_getCommit(
            Address contractAddress,
            UInt256 leafIndex,
            BlockParameter? blockParameter = null)
        {
            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);
            bool isLeafIndexValid = !(leafIndex > BaselineTree.MaxLeafIndex || leafIndex < 0L);

            ResultWrapper<BaselineTreeNode> result;
            if (!isLeafIndexValid)
            {
                result = ResultWrapper<BaselineTreeNode>.Fail(
                    $"{leafIndex} is not a valid leaf index",
                    ErrorCodes.InvalidInput);
            }
            else if (!isTracked || tree == null)
            {
                result = ResultWrapper<BaselineTreeNode>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                if (blockParameter == null)
                {
                    result = ResultWrapper<BaselineTreeNode>.Success(tree.GetLeaf((uint) leafIndex));
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<BaselineTreeNode>.Fail(searchResult);
                    }
                    else
                    {
                        var leaf = _baselineTreeHelper.GetHistoricalLeaf(tree, (uint) leafIndex, searchResult.Object.Number);
                        result = ResultWrapper<BaselineTreeNode>.Success(leaf);
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<long>> baseline_getCount(
            Address contractAddress,
            BlockParameter? blockParameter = null)
        {
            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);

            ResultWrapper<long> result;
            if (!isTracked || tree == null)
            {
                result = ResultWrapper<long>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                if (blockParameter == null)
                {
                    result = ResultWrapper<long>.Success(tree.Count);
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<long>.Fail(searchResult);
                    }
                    else
                    {
                        result = ResultWrapper<long>.Success(tree.GetBlockCount(searchResult.Object.Number));
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getCommits(
            Address contractAddress,
            UInt256[] leafIndexes,
            BlockParameter? blockParameter = null)
        {
            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);
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
            if (!isTracked || tree == null)
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
                var indexes = leafIndexes.Select(i => (uint) i).ToArray();
                if (blockParameter == null)
                {
                    result = ResultWrapper<BaselineTreeNode[]>.Success(
                        tree.GetLeaves(indexes));
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<BaselineTreeNode[]>.Fail(searchResult);
                    }
                    else
                    {
                        var leaves = _baselineTreeHelper.GetHistoricalLeaves(tree, indexes, searchResult.Object.Number);
                        result = ResultWrapper<BaselineTreeNode[]>.Success(leaves);
                    }
                }
            }

            return Task.FromResult(result);
        }

        public async Task<ResultWrapper<Keccak>> baseline_deploy(Address address, string contractType, string? argumentsAbi = null)
        {
            // sample shield arguments:
            // Goerli:  000000000000000000000000b881525d318d6bb54058116af45dd83e7c34fc4e0000000000000000000000000000000000000000000000000000000000000020
            // Ropsten: 000000000000000000000000aba8d681f5391fcf27636416b02a2c748d7b0a9e0000000000000000000000000000000000000000000000000000000000000020

            ResultWrapper<Keccak> result;
            try
            {
                var bytecode = await GetContractBytecode(contractType, argumentsAbi);
                try
                {
                    Keccak txHash = await DeployBytecode(address, contractType, bytecode);
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

        public async Task<ResultWrapper<Keccak>> baseline_deployBytecode(Address address, string byteCode)
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
                    Keccak txHash = await DeployBytecode(address, "bytecode", bytecodeBytes);
                    result = ResultWrapper<Keccak>.Success(txHash);
                }
                catch (Exception e)
                {
                    result = ResultWrapper<Keccak>.Fail(
                        $"Provided bytecode could not be deployed. {e}",
                        ErrorCodes.InternalError);
                }
            }

            return result;
        }

        public Task<ResultWrapper<bool>> baseline_verify(
            Address contractAddress,
            Keccak root,
            Keccak leaf,
            BaselineTreeNode[] path,
            BlockParameter? blockParameter = null)
        {
            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);
            ResultWrapper<bool> result;
            if (!isTracked)
            {
                result = ResultWrapper<bool>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                if (blockParameter == null)
                {
                    bool verificationResult = tree!.Verify(root, leaf, path);
                    result = ResultWrapper<bool>.Success(verificationResult);
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<bool>.Fail(searchResult);
                    }
                    else
                    {
                        var historicalTree = _baselineTreeHelper.CreateHistoricalTree(contractAddress, searchResult.Object.Number);
                        bool verificationResult = historicalTree!.Verify(root, leaf, path);
                        result = ResultWrapper<bool>.Success(verificationResult);
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<BaselineTreeNode[]>> baseline_getSiblings(
            Address contractAddress,
            long leafIndex,
            BlockParameter? blockParameter = null)
        {
            if (leafIndex > BaselineTree.MaxLeafIndex || leafIndex < 0L)
            {
                return Task.FromResult(ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"{leafIndex} is not a valid leaf index",
                    ErrorCodes.InvalidInput));
            }

            ResultWrapper<BaselineTreeNode[]> result;

            bool isTracked = TryGetTracked(contractAddress, out BaselineTree? tree);
            if (!isTracked)
            {
                result = ResultWrapper<BaselineTreeNode[]>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                if (blockParameter == null)
                {
                    result = ResultWrapper<BaselineTreeNode[]>.Success(tree!.GetProof((uint) leafIndex));
                }
                else
                {
                    SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                    if (searchResult.IsError)
                    {
                        result = ResultWrapper<BaselineTreeNode[]>.Fail(searchResult);
                    }
                    else
                    {
                        var historicalTree = _baselineTreeHelper.CreateHistoricalTree(contractAddress, searchResult.Object.Number);
                        result = ResultWrapper<BaselineTreeNode[]>.Success(historicalTree!.GetProof((uint) leafIndex));
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<ResultWrapper<bool>> baseline_track(Address contractAddress)
        {
            ResultWrapper<bool> result;

            // can potentially warn user if tree is not deployed at the address
            if (contractAddress == null)
            {
                result = ResultWrapper<bool>.Fail("Contract address was NULL");
            }
            else if (TryAddTree(contractAddress))
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

        public Task<ResultWrapper<bool>> baseline_untrack(Address contractAddress)
        {
            ResultWrapper<bool> result;

            bool isTracked = TryGetTracked(contractAddress, out _);
            if (!isTracked)
            {
                result = ResultWrapper<bool>.Fail(
                    $"{contractAddress} tree is not tracked",
                    ErrorCodes.InvalidInput);
            }
            else
            {
                bool managedToUntrack = _trackingOverrides.TryUpdate(contractAddress, true, false);
                result = ResultWrapper<bool>.Success(managedToUntrack);
            }

            return Task.FromResult(result);
        }

        private bool TryGetTracked(Address contractAddress, out BaselineTree? tree)
        {
            bool isTracked = _baselineTrees.TryGetValue(contractAddress, out tree);
            return isTracked && (_trackingOverrides.TryGetValue(contractAddress, out bool isUntracked) && !isUntracked);
        }

        public Task<ResultWrapper<Address[]>> baseline_getTracked()
        {
            lock (_metadata)
            {
                return Task.FromResult(ResultWrapper<Address[]>.Success(_metadata.TrackedTrees));
            }
        }

        public async Task<ResultWrapper<VerifyAndPushResponse>> baseline_verifyAndPush(
            Address address,
            Address contractAddress,
            UInt256[] proof,
            UInt256[] publicInputs,
            Keccak newCommitment)
        {
            var txData = _abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                ContractShield.VerifyAndPushSig,
                proof,
                publicInputs,
                newCommitment);

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 20.GWei();

            Keccak txHash = (await _txSender.SendTransaction(tx, TxHandlingOptions)).Hash;
            return ResultWrapper<VerifyAndPushResponse>.Success(new VerifyAndPushResponse(txHash));
        }

        #region private

        private readonly IAbiEncoder _abiEncoder;
        private readonly IFileSystem _fileSystem;
        private readonly IDb _baselineDb;
        private readonly IKeyValueStore _metadataBaselineDb;
        private readonly ILogger _logger;
        private readonly ITxSender _txSender;
        private readonly IStateReader _stateReader;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBaselineTreeHelper _baselineTreeHelper;
        private readonly DisposableStack _disposableStack;

        private BaselineMetadata _metadata;

        private byte[] _metadataKey = {0};

        private ConcurrentDictionary<Address, BaselineTree> _baselineTrees
            = new ConcurrentDictionary<Address, BaselineTree>();

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

        private async Task<Keccak> DeployBytecode(Address address, string contractType, byte[] bytecode)
        {
            Transaction tx = new();
            tx.Value = 0;
            tx.Data = bytecode;
            tx.GasLimit = 1000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = address;

            Keccak txHash = (await _txSender.SendTransaction(tx, TxHandlingOptions)).Hash;

            _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");
            _logger.Info($"Contract {contractType} has been deployed");
            return txHash;
        }

        private void UpdateMetadata(Address contractAddress)
        {
            lock (_metadata)
            {
                var list = _metadata.TrackedTrees.ToList();
                list.Add(contractAddress);
                _metadata.TrackedTrees = list.ToArray();

                _baselineDb[_metadataKey] = SerializeMetadata();
            }
        }

        private byte[] SerializeMetadata()
        {
            lock (_metadata)
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

        /// <summary>
        /// We retrieve the line 3 from here (bytecode) 
        /// 
        /// ======= MerkleTreeSHA.sol:MerkleTreeSHA =======
        /// Binary: 
        /// 608060405234801561001057600080fd5b5061080980610(...)
        /// </summary>
        /// <param name="contract"></param>
        /// <param name="argumentsAbi"></param>
        /// <returns></returns>
        private async Task<byte[]> GetContractBytecode(string contract, string? argumentsAbi)
        {
            // TODO: remove the hack and write code nicely
            string[] contractBytecode = await _fileSystem.File.ReadAllLinesAsync($"plugins/contracts/{contract}.bin".GetApplicationResourcePath());
            if (contractBytecode.Length < 4)
            {
                contractBytecode = await _fileSystem.File.ReadAllLinesAsync($"contracts/{contract}.bin".GetApplicationResourcePath());
                if (contractBytecode.Length < 4)
                {
                    contractBytecode = await _fileSystem.File.ReadAllLinesAsync($"contracts/{contract}.bin");
                    if (contractBytecode.Length < 4)
                    {
                        throw new IOException("Bytecode not found");
                    }
                }
            }

            if (_logger.IsInfo) _logger.Info($"Loading bytecode of {contractBytecode[1]}");
            string bytecodeHex = contractBytecode[3];
            bytecodeHex = bytecodeHex.Replace("#argumentsAbi#", argumentsAbi ?? string.Empty);
            return Bytes.FromHexString(bytecodeHex);
        }

        private void InitTrees()
        {
            foreach (Address trackedTree in _metadata.TrackedTrees)
            {
                TryAddTree(trackedTree);
            }
        }

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
            bool treeAdded = false;

            bool wasUntracked = _trackingOverrides.TryGetValue(trackedTree, out bool result) && result;
            if (!wasUntracked)
            {
                if (_stateReader.GetCode(_blockFinder.Head.StateRoot, trackedTree).Length != 0)
                {
                    ShaBaselineTree tree = new(_baselineDb, _metadataBaselineDb, trackedTree.Bytes, TruncationLength, _logger);
                    treeAdded = _baselineTrees.TryAdd(trackedTree, tree);
                    if (treeAdded)
                    {
                        BaselineTreeTracker? tracker = new(trackedTree, tree, _blockProcessor, _baselineTreeHelper, _blockFinder, _logger);
                        _disposableStack.Push(tracker);
                    }
                }
                
                _trackingOverrides.TryAdd(trackedTree, false);
            }
            else
            {
                treeAdded = _trackingOverrides.TryUpdate(trackedTree, false, true);
            }

            return treeAdded;
        }
        
        private ConcurrentDictionary<Address, bool> _trackingOverrides = new();
        
        private bool TryRemoveTree(Address trackedTree)
        {
            if (_logger.IsWarn) _logger.Warn("Tree untracking has no effect for the moment.");
            // TODO: review if we need to clear metadata and store empty metadata in the database
            // TODO: review if there will be a conflict if we track again later
            // TODO: review if we can drop old databases on untracking
            // TODO: all these todos are fine for now if we do nothing on untrack
            // TODO: the only problem now is if we track, untrack, track again
            
            return true;
        }

        #endregion
    }
}
