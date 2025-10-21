// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NUnit.Framework;
using Nethermind.Evm.EvmObjectFormat;

namespace Ethereum.Test.Base
{
    public abstract class EofTestBase
    {
        private static ILogManager _logManager = new TestLogManager(LogLevel.Warn);
        private static ILogger _logger = _logManager.GetClassLogger();

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

        protected void RunCITest(EofTest test)
        {
            var result = RunTest(test, NullTxTracer.Instance);

            if (result != test.Result.Success)
            {
                _logger.Info($"Spec: {test.Spec}");
                _logger.Info(test.Description);
                _logger.Info($"Url: {test.Url}");
            }

            Assert.That(result, Is.EqualTo(test.Result.Success));
        }

        protected bool RunTest(EofTest test)
        {
            return RunTest(test, NullTxTracer.Instance) == test.Result.Success;
        }

        protected bool RunTest(EofTest test, ITxTracer txTracer)
        {
            _logger.Info($"Running {test.Name} at {DateTime.UtcNow:HH:mm:ss.ffffff}");
            Assert.That(test.LoadFailure, Is.Null, "test data loading failure");

            var vector = test.Vector;
            var code = vector.Code;
            var strategy = vector.ContainerKind;
            var fork = test.Result.Fork switch
            {
                "Osaka" => Nethermind.Specs.Forks.Osaka.Instance,
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

            bool result = CodeDepositHandler.IsValidWithEofRules(fork, code, 1, strategy);
            return result;
        }
    }
}
