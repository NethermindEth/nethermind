// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Xdc.Types;
using NSubstitute;
using Module = Autofac.Module;

namespace Nethermind.Xdc.Test.Helpers;

/// <summary>
/// Create a reasonably complete nethermind configuration.
/// It should not really have any test specific configuration which is set by `TestEnvironmentModule`.
/// May not work without `TestEnvironmentModule`.
/// </summary>
/// <param name="configProvider"></param>
/// <param name="spec"></param>
public class XdcModuleTestOverrides(IConfigProvider configProvider, ILogManager logManager) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();

        base.Load(builder);
        builder
            .AddModule(new XdcModule())
            .AddModule(new PseudoNetworkModule())
            .AddModule(new TestBlockProcessingModule())

            // add missing components
            .AddSingleton<IPenaltyHandler, RandomPenality>()
            .AddSingleton<IForensicsProcessor, TrustyForensics>()

            // Environments
            .AddSingleton<IBackgroundTaskScheduler, IMainProcessingContext, IChainHeadInfoProvider>((blockProcessingContext, chainHeadInfoProvider) => new BackgroundTaskScheduler(
                blockProcessingContext.BranchProcessor,
                chainHeadInfoProvider,
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                logManager))
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()

            // Crypto
            .AddSingleton<ISignerStore>(NullSigner.Instance)
            .AddSingleton(Substitute.For<IKeyStore>())
            .AddSingleton<IWallet, DevWallet>()
            .AddSingleton(Substitute.For<ITxSender>())

            // Rpc
            .AddSingleton<IJsonRpcService, JsonRpcService>()
            ;


        // Yep... this global thing need to work.
        builder.RegisterBuildCallback((_) =>
        {
            var assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
                Rlp.RegisterDecoders(assembly, canOverrideExistingDecoders: true);
        });
    }

    internal class RandomPenality : IPenaltyHandler
    {
        public Address[] Penalize(Address[] candidates, int count = 2)
        {
            var nodesCount = candidates.Length;
            List<Address> penalized = new();

            Random rand = new();
            while (penalized.Count < count && penalized.Count < nodesCount)
            {
                Address candidate = candidates[rand.Next(nodesCount)];
                if (!penalized.Contains(candidate))
                    penalized.Add(candidate);
            }

            return penalized.ToArray();
        }
        public Address[] HandlePenalties(long number, Hash256 currentHash, Address[] candidates)
            => Penalize(candidates, 7);
    }

    internal class TrustyForensics : IForensicsProcessor
    {
        public Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool)
        {
            return Task.CompletedTask;
        }

        public (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath) FindAncestorBlockHash(BlockRoundInfo firstBlockInfo, BlockRoundInfo secondBlockInfo)
        {
            return (Hash256.Zero, new List<string>(), new List<string>());
        }

        public Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC)
        {
            return Task.CompletedTask;
        }

        public Task ProcessForensics(QuorumCertificate incomingQC)
        {
            return Task.CompletedTask;
        }

        public Task ProcessVoteEquivocation(Vote incomingVote)
        {
            return Task.CompletedTask;
        }

        public Task SendForensicProof(QuorumCertificate firstQc, QuorumCertificate secondQc)
        {
            return Task.CompletedTask;
        }

        public Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer)
        {
            return Task.CompletedTask;
        }

        public Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCertificate incomingQC)
        {
            return Task.CompletedTask;
        }
    }
}
