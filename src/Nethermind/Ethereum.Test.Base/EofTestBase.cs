// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.TxPool;
using Nethermind.Evm.EvmObjectFormat;

namespace Ethereum.Test.Base
{
    public abstract class EofTestBase
    {
        private static ILogger _logger = new(new ConsoleAsyncLogger(LogLevel.Info));
        private static ILogManager _logManager = new TestLogManager(LogLevel.Warn);

        [SetUp]
        public void Setup()
        {
            EofValidator.Logger = _logger;
        }

        protected static void Setup(ILogManager logManager)
        {
            _logManager = logManager ?? LimboLogs.Instance;
            _logger = _logManager.GetClassLogger();
        }

        protected bool RunTest(EofTest test)
        {
            return RunTest(test, NullTxTracer.Instance);
        }

        protected bool RunTest(EofTest test, ITxTracer txTracer)
        {
            TestContext.WriteLine($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.IsNull(test.LoadFailure, "test data loading failure");

            var vector = test.Vector;
            var code = vector.Code;
            var fork = test.Result.Fork switch
            {
                "Prague" => Nethermind.Specs.Forks.Prague.Instance,
                "Berlin" => Nethermind.Specs.Forks.Berlin.Instance,
                "London" => Nethermind.Specs.Forks.London.Instance,
                "Shanghai" => Nethermind.Specs.Forks.Shanghai.Instance,
                "Constantinople" => Nethermind.Specs.Forks.Constantinople.Instance,
                "Byzantium" => Nethermind.Specs.Forks.Byzantium.Instance,
                "SpuriousDragon" => Nethermind.Specs.Forks.SpuriousDragon.Instance,
                "TangerineWhistle" => Nethermind.Specs.Forks.TangerineWhistle.Instance,
                "Homestead" => Nethermind.Specs.Forks.Homestead.Instance,
                "Frontier" => Nethermind.Specs.Forks.Frontier.Instance,
                _ => throw new NotSupportedException($"Fork {test.Result.Fork} is not supported")
            };

            bool result = CodeDepositHandler.IsValidWithEofRules(fork, code, 1);

            return result == test.Result.Success;
        }
    }
}
