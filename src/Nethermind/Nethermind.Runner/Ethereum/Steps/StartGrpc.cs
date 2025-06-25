// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork))]
    public class StartGrpc(GrpcRunner grpcRunner, ILogManager logManager) : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            ILogger logger = logManager.GetClassLogger();
            await grpcRunner.Start(cancellationToken).ContinueWith(x =>
            {
                if (x.IsFaulted && logger.IsError)
                    logger.Error("Error during GRPC runner start", x.Exception);
            }, cancellationToken);
        }
    }
}
