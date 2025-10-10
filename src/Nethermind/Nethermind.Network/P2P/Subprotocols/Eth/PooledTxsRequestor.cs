// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//using Nethermind.Core.Extensions;
//using Nethermind.Core.Specs;
//using Nethermind.TxPool;

//namespace Nethermind.Network.P2P.Subprotocols.Eth
//{
//    public class PooledTxsRequestor(ITxPool txPool, ITxPoolConfig txPoolConfig, ISpecProvider specProvider) : IPooledTxsRequestor
//    {

//        private readonly bool _blobSupportEnabled = txPoolConfig.BlobsSupport.IsEnabled();
//        private readonly long _configuredMaxTxSize = txPoolConfig.MaxTxSize ?? long.MaxValue;

//        private readonly long _configuredMaxBlobTxSize = txPoolConfig.MaxBlobTxSize is null
//            ? long.MaxValue
//            : txPoolConfig.MaxBlobTxSize.Value + (long)specProvider.GetFinalMaxBlobGasPerBlock();







//        //private ArrayPoolList<Hash256> AddMarkUnknownHashesEth66(ReadOnlySpan<Hash256> hashes, Action<V66.Messages.GetPooledTransactionsMessage> send, Guid sessionId)
//        //{
//        //    ArrayPoolList<Hash256> discoveredTxHashes = new(hashes.Length);
//        //    for (int i = 0; i < hashes.Length; i++)
//        //    {
//        //        Hash256 hash = hashes[i];
//        //        if (!txPool.IsKnown(hash))
//        //        {
//        //            if (txPool.AnnounceTx(hash, sessionId, () =>
//        //            {
//        //                ArrayPoolList<Hash256> hashesToRetry = new(1) { hash };
//        //                V66.Messages.GetPooledTransactionsMessage msg66 = new(MessageConstants.Random.NextLong(), new GetPooledTransactionsMessage(hashesToRetry));
//        //                send(msg66);
//        //            }) is AnnounceResult.New)
//        //            {
//        //                discoveredTxHashes.Add(hash);
//        //            }
//        //        }
//        //    }

//        //    return discoveredTxHashes;
//        //}

//    }
//}
