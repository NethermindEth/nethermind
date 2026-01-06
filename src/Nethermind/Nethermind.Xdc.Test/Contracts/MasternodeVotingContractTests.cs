// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Xdc.Contracts;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using static Nethermind.Consensus.Processing.AutoReadOnlyTxProcessingEnvFactory;

namespace Nethermind.Xdc.Test;

internal class MasternodeVotingContractTests
{
    [Test]
    public void GetCandidatesAndStake_GenesisSetup_CanReadExpectedCandidates()
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        PrivateKey signer = TestItem.PrivateKeyB;
        Address codeSource = TestItem.AddressC;

        ISpecProvider specProvider = new TestSpecProvider(Shanghai.Instance);
        IDbProvider memDbProvider = TestMemDbProvider.Init();
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest(memDbProvider, LimboLogs.Instance);

        IReleaseSpec finalSpec = specProvider.GetFinalSpec();
        BlockHeader genesis;
        using (IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(sender.Address, 1.Ether());
            byte[] code = XdcContractData.XDCValidatorBin();
            stateProvider.CreateAccountIfNotExists(codeSource, 0);
            stateProvider.InsertCode(codeSource, ValueKeccak.Compute(code), code, Shanghai.Instance);

            Dictionary<string, string> storage = GenesisAllocation;
            foreach (KeyValuePair<string, string> kvp in storage)
            {
                StorageCell cell = new(codeSource, UInt256.Parse(kvp.Key));
                stateProvider.Set(cell, Bytes.FromHexString(kvp.Value));
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            stateProvider.CommitTree(0);

            genesis = Build.A.XdcBlockHeader().WithStateRoot(stateProvider.StateRoot).TestObject;
        }

        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor transactionProcessor = new(BlobBaseFeeCalculator.Instance, specProvider, stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        MasternodeVotingContract masterVoting = new(new AbiEncoder(), codeSource, new AutoReadOnlyTxProcessingEnv(transactionProcessor, stateProvider, Substitute.For<ILifetimeScope>()));

        Address[] candidates = masterVoting.GetCandidates(genesis);
        candidates.Length.Should().Be(3);

        foreach (Address candidate in candidates)
        {
            UInt256 stake = masterVoting.GetCandidateStake(genesis, candidate);
            stake.Should().Be(10_000_000.Ether());
        }
    }

    private static Dictionary<string, string> GenesisAllocation =
        new Dictionary<string, string>
        {
            ["0x0000000000000000000000000000000000000000000000000000000000000007"] = "0x0000000000000000000000000000000000000000000000000000000000000001",
            ["0x0000000000000000000000000000000000000000000000000000000000000008"] = "0x0000000000000000000000000000000000000000000000000000000000000003",
            ["0x0000000000000000000000000000000000000000000000000000000000000009"] = "0x0000000000000000000000000000000000000000000000000000000000000003",
            ["0x000000000000000000000000000000000000000000000000000000000000000a"] = "0x0000000000000000000000000000000000000000000000000000000000000001",
            ["0x000000000000000000000000000000000000000000000000000000000000000b"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0x000000000000000000000000000000000000000000000000000000000000000c"] = "0x00000000000000000000000000000000000000000000054b40b1f852bda00000",
            ["0x000000000000000000000000000000000000000000000000000000000000000d"] = "0x0000000000000000000000000000000000000000000000000000000000000012",
            ["0x000000000000000000000000000000000000000000000000000000000000000e"] = "0x000000000000000000000000000000000000000000000000000000000013c680",
            ["0x000000000000000000000000000000000000000000000000000000000000000f"] = "0x0000000000000000000000000000000000000000000000000000000000069780",
            ["0x1cb68bf63bb3b55abf504ef789bb06e8b2b266a334ca39892e163225a47b8267"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0x2c6b8fd5b2b39958a7e5a98eebf2c1c31122e89c7961ce1025e69a3d3f07fd20"] = "0x0000000000000000000000000000000000000000000000000000000000000001",
            ["0x3639e2dfabac2c6baff147abd66f76b8e526e974a9a2a14163169aa03d2f8d4b"] = "0x0000000000000000000000000000000000000000000000000000000000000001",
            ["0x473ba2a6d1aa200b3118a8abc51fe248a479e882e6c655ae014d9c66fbc181ed"] = "0x00000000000000000000000025c65b4b379ac37cf78357c4915f73677022eaff",
            ["0x473ba2a6d1aa200b3118a8abc51fe248a479e882e6c655ae014d9c66fbc181ee"] = "0x000000000000000000000000c7d49d0a2cf198deebd6ce581af465944ec8b2bb",
            ["0x473ba2a6d1aa200b3118a8abc51fe248a479e882e6c655ae014d9c66fbc181ef"] = "0x000000000000000000000000cfccdea1006a5cfa7d9484b5b293b46964c265c0",
            ["0x53dbb2c13e64ef254df4bb7c7b541e84dd24870927f98f151db88daa464fb4dc"] = "0x000000000000000000000000381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0x67a3292220e327ce969d100d7e4d83dd4b05efa763a5e4cdb04e0c0107736472"] = "0x000000000000000000000001381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0x67a3292220e327ce969d100d7e4d83dd4b05efa763a5e4cdb04e0c0107736473"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0x78dfe8da08db00fe2cd4ddbd11f9cb7e4245ce35275d7734678593942034e181"] = "0x000000000000000000000001381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0x78dfe8da08db00fe2cd4ddbd11f9cb7e4245ce35275d7734678593942034e182"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0x90e333b6971c3ecd09a0da09b031d63cdd2dc213d199a66955a8bf7df8a8142d"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0xa66cc928b5edb82af9bd49922954155ab7b0942694bea4ce44661d9a8736c688"] = "0x000000000000000000000000381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0xac80bed7555f6f181a34915490d97d0bfe2c0e116d1c73b34523ca0d9749955c"] = "0x000000000000000000000000381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0xae7e2a864ae923819e93a9f6183bc7ca0dcee93a0759238acd92344ad3216228"] = "0x000000000000000000000000000000000000000000084595161401484a000000",
            ["0xb375859c4c97d60e8a699586dc5dd215f38f99e40430bb9261f085ee694ffb2c"] = "0x0000000000000000000000000000000000000000000000000000000000000001",
            ["0xd5d5b62da76a3a9f2df0e9276cbaf8973a778bf41f7f4942e06243f195493e99"] = "0x000000000000000000000000381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0xec8699f61c2c8bbdbc66463590788e526c60046dda98e8c70df1fb756050baa4"] = "0x0000000000000000000000000000000000000000000000000000000000000003",
            ["0xf3f7a9fe364faab93b216da50a3214154f22a0a2b415b23a84c8169e8b636ee3"] = "0x00000000000000000000000025c65b4b379ac37cf78357c4915f73677022eaff",
            ["0xf3f7a9fe364faab93b216da50a3214154f22a0a2b415b23a84c8169e8b636ee4"] = "0x000000000000000000000000c7d49d0a2cf198deebd6ce581af465944ec8b2bb",
            ["0xf3f7a9fe364faab93b216da50a3214154f22a0a2b415b23a84c8169e8b636ee5"] = "0x000000000000000000000000cfccdea1006a5cfa7d9484b5b293b46964c265c0",
            ["0xf4dd36495f675c407ac8f8d6dd8cc40162c854dba3ce4ce8919af34d0b1ed47c"] = "0x000000000000000000000001381047523972c9fdc3aa343e0b96900a8e2fa765",
            ["0xf4dd36495f675c407ac8f8d6dd8cc40162c854dba3ce4ce8919af34d0b1ed47d"] = "0x000000000000000000000000000000000000000000084595161401484a000000"
        };
}
