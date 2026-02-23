// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
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

            .AddSingleton<IMasternodeVotingContract, XdcTestDepositContract>()

            // add missing components
            .AddSingleton<IPenaltyHandler, RandomPenaltyHandler>()
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

    internal class RandomPenaltyHandler(ISpecProvider specProvider) : IPenaltyHandler
    {
        readonly Dictionary<Hash256, Address[]> _penaltiesCache = new();
        public Address[] Penalize(long number, Hash256 currentHash, Address[] candidates, int count = 2)
        {
            var spec = specProvider.GetFinalSpec() as IXdcReleaseSpec ?? throw new ArgumentException("Must have XDC spec configured.");
            if (number == spec.SwitchBlock)
            {
                return Array.Empty<Address>();
            }
            if (_penaltiesCache.ContainsKey(currentHash))
            {
                return _penaltiesCache[currentHash];
            }
            var nodesCount = candidates.Length;
            List<Address> penalized = new();

            Random rand = new();
            while (penalized.Count < count && penalized.Count < nodesCount)
            {
                Address candidate = candidates[rand.Next(nodesCount)];
                if (!penalized.Contains(candidate))
                    penalized.Add(candidate);
            }
            _penaltiesCache[currentHash] = penalized.ToArray();
            return _penaltiesCache[currentHash];
        }
        public Address[] HandlePenalties(long number, Hash256 currentHash, Address[] candidates)
            => Penalize(number, currentHash, candidates, 7);
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
