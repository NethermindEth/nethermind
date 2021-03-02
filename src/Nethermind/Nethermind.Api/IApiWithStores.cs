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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Db.Blooms;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Api
{
    public interface IApiWithStores : IBasicApi
    {
        IBlockTree? BlockTree { get; set; }
        IBloomStorage? BloomStorage { get; set; }
        IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        ILogFinder? LogFinder { get; set; }
        ISigner? EngineSigner { get; set; }
        ISignerStore? EngineSignerStore { get; set; }
        ProtectedPrivateKey? NodeKey { get; set; }
        IReceiptStorage? ReceiptStorage { get; set; }
        IReceiptFinder? ReceiptFinder { get; set; }
        IWallet? Wallet { get; set; }
    }
}
