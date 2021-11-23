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
using Nethermind.AccountAbstraction.Flashbots;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    public class ContinuousBundleSender
    {
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly ISigner _signer;
        private readonly ITimer _timer;
        private readonly UserOperationTxSource _userOperationTxSource;

        public ContinuousBundleSender
        (
            IBlockTree blockTree,
            UserOperationTxSource userOperationTxSource,
            IAccountAbstractionConfig accountAbstractionConfig,
            ITimerFactory timerFactory,
            ISigner signer,
            ILogger logger
        )
        {
            _blockTree = blockTree;
            _accountAbstractionConfig = accountAbstractionConfig;
            _signer = signer;
            _logger = logger;
            _userOperationTxSource = userOperationTxSource;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(5000));
            _timer.Elapsed += TimerOnElapsed!;
            _timer.AutoReset = false;
            _timer.Start();
        }

        private void TimerOnElapsed(object sender, EventArgs args)
        {
            FlashbotsSender flashbotsSender = new(new HttpClient(), _signer, _logger);

            // turn ops into txs
            /* IEnumerable<Transaction> transaction = */
            /*     _userOperationTxSource.GetTransactions(_blockTree.Head!.Header, _blockTree.Head.GasLimit); */
            // FIXME: This is only here to prevent compiler errors
            IEnumerable<Transaction> transaction =
                new List<Transaction> { _userOperationTxSource.GetTransaction(_blockTree.Head!.Header, (ulong)_blockTree.Head.GasLimit)! };
            string[] transactionArray = transaction.Select(pkg => pkg.ToString()).ToArray();
            // turn txs into MevBundle
            FlashbotsSender.MevBundle bundle = new(_blockTree.Head.Header.Number + 1, transactionArray);

            // send MevBundle using SendBundle()
            flashbotsSender.SendBundle(bundle, _accountAbstractionConfig.FlashbotsEndpoint).ContinueWith(_ => _);

            _timer.Enabled = true;
        }
    }
}
