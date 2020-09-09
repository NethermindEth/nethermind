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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Runner.Extensions
{
    public interface IBlockTreePlugin : IPlugin
    {
        public IBlockTreeVisitor Visitor { get; }
    }

    public class TotalFeesByBlockPlugin : IBlockTreePlugin
    {
        public void Dispose()
        {
            throw new System.NotImplementedException();
        }

        public string Name => "Total Fees";
        public string Description => "Outputs Total Gas Fees By Block";
        public string Author => "Nethermind";
        
        public void Init(INethermindApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public IBlockTreeVisitor Visitor { get; }

        private INethermindApi _api;
    }
    
    public class TotalFeesByBlock : IBlockTreeVisitor
    {
        private readonly IReceiptStorage _receiptStorage;
        public bool PreventsAcceptingNewBlocks { get; }
        public long StartLevelInclusive { get; }
        public long EndLevelExclusive { get; }

        public TotalFeesByBlock(IReceiptStorage receiptStorage)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        }        
        
        public Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public Task<bool> VisitMissing(Keccak hash, CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            return Task.FromResult(HeaderVisitOutcome.None);
        }

        public Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
        {
            return Task.FromResult(BlockVisitOutcome.None);
        }

        public Task<LevelVisitOutcome> VisitLevelEnd(CancellationToken cancellationToken)
        {
            return Task.FromResult(LevelVisitOutcome.None);
        }
    }
}