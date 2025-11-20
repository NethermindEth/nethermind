// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Consensus.Processing.AutoReadOnlyTxProcessingEnvFactory;

namespace Nethermind.Xdc.Test;
internal class XdcContractTests
{
    [Test]
    public void MasternodeVotingContract()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        ISpecProvider specProvider = new TestSpecProvider(Shanghai.Instance);
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest(memDbProvider, LimboLogs.Instance);

        IReleaseSpec finalSpec = specProvider.GetFinalSpec();

        using (var _ = stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(sender.Address, 1.Ether());
            byte[] code = XdcContractData.XDCValidatorBin();
            stateProvider.CreateAccountIfNotExists(codeSource, 0);
            stateProvider.InsertCode(codeSource, ValueKeccak.Compute(code), code, Shanghai.Instance);
            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);

            var genesis = Build.A.BlockHeader.WithStateRoot(stateProvider.StateRoot).TestObject;
        }

        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        var transactionProcessor = new TransactionProcessor(BlobBaseFeeCalculator.Instance, specProvider, stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        //Save caller in storage slot 0

        var masterVoting = new MasternodeVotingContract(stateProvider, new AbiEncoder(), codeSource, new AutoReadOnlyTxProcessingEnv(transactionProcessor, stateProvider, Substitute.For<ILifetimeScope>()));

        var header = Build.A.BlockHeader.TestObject;
        var candidates = masterVoting.GetCandidates(header);
    }
}
