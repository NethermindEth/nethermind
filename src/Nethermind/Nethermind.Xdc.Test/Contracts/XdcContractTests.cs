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
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
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
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _worldStateForContract;
    private IWorldState _worldState;

    [SetUp]
    public async Task Setup()
    {
        var dbProvider = await TestMemDbProvider.InitAsync();

        _specProvider = new TestSpecProvider(Shanghai.Instance);
        _worldState = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, NullLogManager.Instance).GlobalWorldState;
        _worldStateForContract = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, NullLogManager.Instance).GlobalWorldState;
        EthereumCodeInfoRepository codeInfoRepository = new(_worldState);
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _worldState, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [Test]
    public void MasternodeVotingContract()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        //Save caller in storage slot 0

        var masterVoting = new MasternodeVotingContract(_worldState, new AbiEncoder(), codeSource, new AutoReadOnlyTxProcessingEnv(_transactionProcessor, _worldState, Substitute.For<ILifetimeScope>()));

        var scope = _worldState.BeginScope(IWorldState.PreGenesis);
        _worldState.CreateAccount(sender.Address, 1.Ether());
        byte[] code = XdcContractData.XDCValidatorBin();
        _worldState.CreateAccountIfNotExists(codeSource, 0);
        _worldState.InsertCode(codeSource, ValueKeccak.Compute(code), code, Shanghai.Instance);
        _worldState.Commit (Shanghai.Instance, true);

        var header = Build.A.BlockHeader.TestObject;
        var candidates = masterVoting.GetCandidates(header);
    }
}
