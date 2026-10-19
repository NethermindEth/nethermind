// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.TimeoutTestHelper;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    // Rents from the static timeout pool, which a method-level NonParallelizable does not isolate from other fixtures.
    [NonParallelizable]
    public class TimeoutPool
    {
        [Test]
        public async Task Eth_get_logs_stream_mode_transfers_timeout_until_response_disposal()
        {
            using Context ctx = await Context.Create();
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            CancellationToken observedToken = default;
            bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    observedToken = call.ArgAt<CancellationToken>(3);
                    return new[] { CreateTestFilterLog() };
                });

            ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: true).WithBlockchainBridge(bridge).Build();
            TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

            try
            {
                CancellationToken expectedToken = timeout.Token;
                ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter());

                try
                {
                    Assert.That(observedToken, Is.EqualTo(expectedToken));
                    Assert.That(response.Data, Is.TypeOf<LogsStreamableResult>());
                    Assert.That(timeout.DisposeCount, Is.EqualTo(0));
                }
                finally
                {
                    response.Dispose();
                }

                Assert.That(timeout.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                DisposeIfNotAlreadyObserved(timeout);
            }
        }

        [Test]
        public async Task Eth_get_logs_returns_timeout_to_pool_when_returning_before_transfer([Values] bool enableLogsStreamMode)
        {
            using Context ctx = await Context.Create();
            ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode).Build();
            TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

            try
            {
                ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter
                {
                    FromBlock = new BlockParameter(2),
                    ToBlock = new BlockParameter(1)
                });

                try
                {
                    Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
                    Assert.That(response.Result.Error, Is.EqualTo("invalid block range params"));
                }
                finally
                {
                    response.Dispose();
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(TryTakeFromPool(timeout), Is.True);
                    Assert.That(timeout.DisposeCount, Is.Zero);
                }
            }
            finally
            {
                DisposeIfNotAlreadyObserved(timeout);
            }
        }

        [Test]
        public async Task Eth_get_logs_stream_mode_disposes_timeout_after_enumeration_failure()
        {
            using Context ctx = await Context.Create();
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            CancellationToken observedToken = default;
            bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    observedToken = call.ArgAt<CancellationToken>(3);
                    return GetLogs();
                });

            ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: true).WithBlockchainBridge(bridge).Build();
            TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

            try
            {
                CancellationToken expectedToken = timeout.Token;
                ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter());
                response.Id = new JsonRpcId(67L);
                string serialized;

                try
                {
                    Assert.That(observedToken, Is.EqualTo(expectedToken));
                    serialized = await WriteJsonRpcResponseAsync(response);
                    Assert.That(timeout.DisposeCount, Is.EqualTo(0));
                }
                finally
                {
                    response.Dispose();
                }

                Assert.That(serialized, Is.EqualTo(ExpectedFilterLogStreamResponse("failed")));
                Assert.That(timeout.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                DisposeIfNotAlreadyObserved(timeout);
            }

            static IEnumerable<FilterLog> GetLogs()
            {
                yield return CreateTestFilterLog();
                throw new InvalidOperationException("Stream mode failed after a complete log item.");
            }
        }
    }
}
