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
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Shanghai.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [Test]
    public void MasternodeVotingContract()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;
        var scope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(sender.Address, 1.Ether());
        //Save caller in storage slot 0
        byte[] code = XdcContractData.XDCValidatorBin();

        DeployCode(codeSource, code);
        scope.Dispose();

        var masterVoting = new MasternodeVotingContract(_stateProvider, new AbiEncoder(), codeSource, new AutoReadOnlyTxProcessingEnv(_transactionProcessor, _stateProvider, Substitute.For<ILifetimeScope>()));

        var header = Build.A.BlockHeader.TestObject;
        var candidates = masterVoting.GetCandidates(header);
    }

    private void DeployCode(Address codeSource, byte[] code)
    {
        _stateProvider.CreateAccountIfNotExists(codeSource, 0);
        _stateProvider.InsertCode(codeSource, ValueKeccak.Compute(code), code, Shanghai.Instance);
    }
}
