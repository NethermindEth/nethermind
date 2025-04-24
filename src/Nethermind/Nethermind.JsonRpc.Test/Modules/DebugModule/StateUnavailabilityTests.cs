// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.DebugModule
{
    [TestFixture]
    public class StateUnavailabilityTests
    {
        private IDebugBridge _debugBridge;
        private IJsonRpcConfig _jsonRpcConfig;
        private IStateReader _stateReader;
        private IBlockFinder _blockFinder;
        private BlockHeader _blockHeader;
        private BlockParameter _blockParameter;
        private DebugRpcModule _debugRpcModule;

        [SetUp]
        public void Setup()
        {
            _debugBridge = Substitute.For<IDebugBridge>();
            _jsonRpcConfig = new JsonRpcConfig();
            _stateReader = Substitute.For<IStateReader>();
            _blockFinder = Substitute.For<IBlockFinder>();

            _blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            _blockParameter = new BlockParameter(_blockHeader.Hash!);

            _blockFinder.FindHeader(_blockParameter).Returns(_blockHeader);

            _debugRpcModule = new DebugRpcModule(
                LimboLogs.Instance,
                _debugBridge,
                _jsonRpcConfig,
                Substitute.For<Core.Specs.ISpecProvider>(),
                Substitute.For<Facade.IBlockchainBridge>(),
                Substitute.For<BlocksConfig>().SecondsPerSlot,
                _blockFinder,
                _stateReader
            );
        }

        [Test]
        public void Debug_traceTransaction_returns_error_when_state_unavailable()
        {
            // Arrange
            var txHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.WithHash(txHash).TestObject;
            var receipt = Build.A.Receipt.TestObject;
            receipt.BlockHash = _blockHeader.Hash;

            _debugBridge.GetTransactionFromHash(txHash).Returns(transaction);
            _debugBridge.GetReceiptsForBlock(_blockParameter).Returns(new[] { receipt });
            _stateReader.HasStateForBlock(_blockHeader).Returns(false);

            // Act
            var result = _debugRpcModule.debug_traceTransaction(txHash);

            // Assert
            Assert.That(result.Data, Is.Null);
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Result.Error, Does.Contain("Cannot find block for transaction hash:"));
        }

        [Test]
        public void Debug_traceCall_returns_error_when_state_unavailable()
        {
            // Arrange
            var txForRpc = Substitute.For<Facade.Eth.RpcTransaction.TransactionForRpc>();
            _stateReader.HasStateForBlock(_blockHeader).Returns(false);

            // Act
            var result = _debugRpcModule.debug_traceCall(txForRpc, _blockParameter);

            // Assert
            Assert.That(result.Data, Is.Null);
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
            Assert.That(result.Result.Error, Does.Contain("Incorrect head block"));
        }

        [Test]
        public void Debug_traceBlockByHash_returns_error_when_state_unavailable()
        {
            // Arrange
            _stateReader.HasStateForBlock(_blockHeader).Returns(false);

            // Act
            var result = _debugRpcModule.debug_traceBlockByHash(_blockHeader.Hash!);

            // Assert
            Assert.That(result.Data, Is.Null);
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
            Assert.That(result.Result.Error, Does.Contain("Incorrect head block"));
        }

        [Test]
        public void Debug_standardTraceBlockToFile_returns_error_when_state_unavailable()
        {
            // Arrange
            _stateReader.HasStateForBlock(_blockHeader).Returns(false);

            // Act
            var result = _debugRpcModule.debug_standardTraceBlockToFile(_blockHeader.Hash!);

            // Assert
            Assert.That(result.Data, Is.Null);
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
            Assert.That(result.Result.Error, Does.Contain("Incorrect head block"));
        }
    }
}
