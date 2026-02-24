// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.JsonRpc.Test.Modules
{
    public abstract class TraceRpcModuleTestsBase
    {
        protected ITraceRpcModule TraceRpcModule { get; private set; } = null!;
        protected IJsonRpcConfig JsonRpcConfig { get; private set; } = null!;
        protected TestRpcBlockchain Blockchain { get; private set; } = null!;

        protected async Task SetupAsync(ISpecProvider? specProvider = null, bool isAura = false)
        {
            JsonRpcConfig = new JsonRpcConfig();
            Blockchain = await TestRpcBlockchain.ForTest(isAura ? SealEngineType.AuRa : SealEngineType.NethDev).Build(specProvider);

            await Blockchain.AddFunds(TestItem.AddressA, 1000.Ether());
            await Blockchain.AddFunds(TestItem.AddressB, 1000.Ether());
            await Blockchain.AddFunds(TestItem.AddressC, 1000.Ether());

            for (int i = 1; i < 10; i++)
            {
                List<Transaction> transactions = new();
                for (int j = 0; j < i; j++)
                {
                    transactions.Add(Build.A.Transaction
                        .WithTo(Address.Zero)
                        .WithNonce(Blockchain.StateReader.GetNonce(Blockchain.BlockTree.Head!.Header, TestItem.AddressB) + (UInt256)j)
                        .SignedAndResolved(Blockchain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject);
                }
                await Blockchain.AddBlockMayMissTx(transactions.ToArray());
            }

            TraceRpcModule = Blockchain.TraceRpcModule;
        }

        protected async Task SetupWithSpecAsync<T>() where T : IReleaseSpec, new()
        {
            await SetupAsync(new TestSpecProvider(new T()));
        }

        protected static void AssertValidTraceResult<T>(ResultWrapper<T> result)
        {
            result.Data.Should().NotBeNull();
        }

        protected static void AssertValidTraceWithContent<T>(ResultWrapper<IEnumerable<T>> result)
        {
            result.Data.Should().NotBeNull().And.NotBeEmpty();
        }

        protected static ForkActivationParameter CreateForkParameter(string forkName) => new() { ForkName = forkName };
        protected static ForkActivationParameter CreateForkParameter(long activationBlock) => new() { ActivationBlock = activationBlock };
        protected static ForkActivationParameter CreateForkParameter(ulong activationTimestamp) => new() { ActivationTimestamp = activationTimestamp };
    }
}