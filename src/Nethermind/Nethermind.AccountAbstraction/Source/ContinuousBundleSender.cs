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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Flashbots;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    public class ContinuousBundleSender
    {
        private readonly IBlockTree _blockTree;
        private readonly UserOperationTxSource _userOperationTxSource;
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly ITimer _timer;
        
        public ContinuousBundleSender
        (
            IBlockTree blockTree, 
            UserOperationTxSource userOperationTxSource, 
            IAccountAbstractionConfig accountAbstractionConfig, 
            ITimerFactory timerFactory
        )
        {
            _blockTree = blockTree;
            _accountAbstractionConfig = accountAbstractionConfig;
            _userOperationTxSource = userOperationTxSource;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(5000));
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start(); 
        }
        
        private void TimerOnElapsed(object sender, EventArgs args)
        {
            INethermindApi _nethermindApi = null!;
            ILogger _logger = null!;
            FlashbotsSender flashbotsSender = new FlashbotsSender(new HttpClient(), _nethermindApi.EngineSigner, _logger);
            
            // turn ops into txs
            IEnumerable<Transaction> transaction = _userOperationTxSource.GetTransactions(_blockTree.Head.Header, _blockTree.Head.GasLimit);
            string[] transactionArray = transaction.Select(pkg => pkg.ToString()).ToArray();
            // turn txs into MevBundle
            FlashbotsSender.MevBundle bundle = new FlashbotsSender.MevBundle(_blockTree.Head.Header.Number + 1, transactionArray);
            
            // send MevBundle using SendBundle()
            flashbotsSender.SendBundle(bundle, _accountAbstractionConfig.FlashbotsEndpoint);

            _timer.Enabled = true;
        }
    }
}
