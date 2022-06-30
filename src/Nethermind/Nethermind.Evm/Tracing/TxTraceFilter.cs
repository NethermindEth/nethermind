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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;

namespace Nethermind.Evm.Tracing
{
    public class TxTraceFilter
    {
        private readonly Address[]? _fromAddresses;
        private readonly Address[]? _toAddresses;
        private int _after;
        private int? _count;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly EthereumEcdsa _ecdsa;
        
        public TxTraceFilter(
            Address[]? fromAddresses,
            Address[]? toAddresses,
            int after,
            int? count,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _fromAddresses = fromAddresses;
            _toAddresses = toAddresses;
            _after = after;
            _count = count;
            _specProvider = specProvider;
            _logger = logManager.GetClassLogger();
            _ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        }

        public bool ShouldTraceTx(Transaction? tx, bool validateChainId)
        {
            if (tx == null || (_count <= 0))
            {
                return false;
            }
            return true;
        }
        
        public bool IsValidTxTrace(ParityTraceAction? tx)
        {
            if (_logger.IsTrace) _logger.Trace($"Tracing transaction {tx}, from: {tx?.From}, to: {tx?.To}, fromAddresses: {_fromAddresses}, toAddresses {_toAddresses}, after {_after}, count {_count}");
            if (tx == null || !MatchAddresses(tx.From, tx.To) ||
                (_count <= 0))
            {
                return false;
            }

            if (_after > 0)
            {
                --_after;
                return false;
            }
            
            --_count;
            return true;
        }

        public bool ShouldTraceBlock(Block? block)
        {
            if (block == null)
                return false;
            return true;
        }

        private bool MatchAddresses(Address? fromAddress, Address? toAddress)
        {
            return (_fromAddresses == null || _fromAddresses.Contains(fromAddress)) &&
                   (_toAddresses == null || _toAddresses.Contains(toAddress));
        }
    }
}
