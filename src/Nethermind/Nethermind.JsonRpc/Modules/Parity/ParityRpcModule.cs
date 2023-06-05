// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.KeyStore;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityRpcModule : IParityRpcModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IEnode _enode;
        private readonly ISignerStore _signerStore;
        private readonly IKeyStore _keyStore;
        private readonly ISpecProvider _specProvider;
        private readonly IPeerManager _peerManager;

        public ParityRpcModule(
            IEcdsa ecdsa,
            ITxPool txPool,
            IBlockFinder blockFinder,
            IReceiptFinder receiptFinder,
            IEnode enode,
            ISignerStore signerStore,
            IKeyStore keyStore,
            ISpecProvider specProvider,
            IPeerManager peerManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _enode = enode ?? throw new ArgumentNullException(nameof(enode));
            _signerStore = signerStore ?? throw new ArgumentNullException(nameof(signerStore));
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions(Address? address = null)
        {
            IEnumerable<Transaction> enumerable = address is null
                 ? _txPool.GetPendingTransactions()
                 : _txPool.GetPendingTransactionsBySender(address);
            return ResultWrapper<ParityTransaction[]>.Success(enumerable
                .Select(t => new ParityTransaction(t, Rlp.Encode(t).Bytes,
                t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
        }

        public ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptForRpc[]>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            TxReceipt[] receipts = _receiptFinder.Get(block) ?? new TxReceipt[block.Transactions.Length];
            bool isEip1559Enabled = _specProvider.GetSpec(block.Header).IsEip1559Enabled;
            IEnumerable<ReceiptForRpc> result = receipts
                .Zip(block.Transactions, (r, t) =>
                {
                    return new ReceiptForRpc(t.Hash, r, t.GetGasInfo(isEip1559Enabled, block.Header), receipts.GetBlockLogFirstIndex(r.Index));
                });
            ReceiptForRpc[] resultAsArray = result.ToArray();
            return ResultWrapper<ReceiptForRpc[]>.Success(resultAsArray);
        }

        public ResultWrapper<bool> parity_setEngineSigner(Address address, string password)
        {
            (ProtectedPrivateKey privateKey, Result result) = _keyStore.GetProtectedKey(address, password.Secure());
            if (result == Result.Success)
            {
                _signerStore.SetSigner(privateKey);
                return ResultWrapper<bool>.Success(true);
            }
            else
            {
                return ResultWrapper<bool>.Success(false);
            }
        }

        public ResultWrapper<bool> parity_setEngineSignerSecret(string privateKey)
        {
            var key = new PrivateKey(privateKey);
            _signerStore.SetSigner(key);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<bool> parity_clearEngineSigner()
        {
            _signerStore.SetSigner((ProtectedPrivateKey)null);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<string> parity_enode() => ResultWrapper<string>.Success(_enode.ToString());

        public ResultWrapper<ParityNetPeers> parity_netPeers()
        {
            ParityNetPeers parityNetPeers = new();
            parityNetPeers.Active = _peerManager.ActivePeers.Count;
            parityNetPeers.Connected = _peerManager.ConnectedPeers.Count;
            parityNetPeers.Max = _peerManager.MaxActivePeers;
            parityNetPeers.Peers = _peerManager.ActivePeers.Select(p => new PeerInfo(p)).ToArray();
            return ResultWrapper<ParityNetPeers>.Success(parityNetPeers);
        }
    }
}
