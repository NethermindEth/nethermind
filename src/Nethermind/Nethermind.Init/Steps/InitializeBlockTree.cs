// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Find;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitTxTypesAndRlp), typeof(InitDatabase), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IServiceStopper _stopper;
        private readonly IBasicApi _get;
        private readonly IApiWithStores _set;
        private readonly INethermindApi _api;

        public InitializeBlockTree(INethermindApi api, IServiceStopper stopper)
        {
            _stopper = stopper;
            (_get, _set) = api.ForInit;
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IInitConfig initConfig = _get.Config<IInitConfig>();

            ISigner signer = NullSigner.Instance;
            ISignerStore signerStore = NullSigner.Instance;
            if (_get.Config<IMiningConfig>().Enabled)
            {
                Signer signerAndStore = new(_get.SpecProvider!.ChainId, _get.OriginalSignerKey!, _get.LogManager);
                signer = signerAndStore;
                signerStore = signerAndStore;
            }

            _set.EngineSigner = signer;
            _set.EngineSignerStore = signerStore;

            if (initConfig.ExitOnBlockNumber is not null)
            {
                new ExitOnBlockNumberHandler(_api.BlockTree, _get.ProcessExit!, initConfig.ExitOnBlockNumber.Value, _get.LogManager);
            }

            return Task.CompletedTask;
        }
    }
}
