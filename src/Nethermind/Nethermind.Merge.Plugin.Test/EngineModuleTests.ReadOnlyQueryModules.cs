// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task A_read_only_query_module_for_every_kind_reaches_no_engine_path()
    {
        FrameOfferProbe probe = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Prague.Instance,
            configurer: builder => builder.AddSingleton<IReadOnlyQueryModule>(probe.ModuleFor(ReadOnlyQueryKind.All)));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        byte[] initCode = Prepare.EvmCode.ForInitOf(Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done).Done;
        Block head = chain.BlockTree.Head!;
        ulong nonce = chain.StateReader.GetNonce(head.Header, TestItem.AddressB);

        // The deployment's init code and the block's beacon root system call run in the payload builder, in newPayload
        // and in the witness re-execution.
        chain.AddTransactions(Build.A.Transaction.WithCode(initCode).WithNonce(nonce).WithGasLimit(1_000_000)
            .WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).WithType(TxType.EIP1559)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject);
        PayloadAttributes attributes = new()
        {
            Timestamp = head.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE,
        };
        Task improved = chain.WaitForImprovedBlock(head.Hash!, 1);
        string payloadId = (await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(head.Hash!, head.Hash!, head.Hash!), attributes)).Data.PayloadId!;
        await improved;
        GetPayloadV4Result built = (await rpc.engine_getPayloadV4(Bytes.FromHexString(payloadId))).Data!;
        ExecutionPayloadV3 payload = built.ExecutionPayload;
        byte[][] requests = built.ExecutionRequests ?? [];
        int afterProduction = probe.Offers;

        using ResultWrapper<NewPayloadWithWitnessV1Result> imported = await rpc.engine_newPayloadWithWitnessV4(payload, [], TestItem.KeccakE, requests);
        int afterImport = probe.Offers;

        // A fresh handler has no recent witnesses, so it re-executes the known block in the witness environments.
        using ILifetimeScope scope = chain.Container.BeginLifetimeScope(builder =>
            builder.RegisterType<NewPayloadWithWitnessHandler>().AsSelf().SingleInstance());
        using ResultWrapper<NewPayloadWithWitnessV1Result> regenerated = await scope.Resolve<NewPayloadWithWitnessHandler>()
            .HandleAsync(new ExecutionPayloadParams<ExecutionPayloadV3>(payload, [], TestItem.KeccakE, requests));
        int afterRegeneration = probe.Offers;

        Transaction query = Build.A.Transaction.WithCode(initCode).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        CallOutput call = chain.Container.Resolve<IBlockchainBridge>().Call(chain.BlockTree.Head!.Header, query);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions, Has.Length.EqualTo(1), "precondition: the payload carries the deployment");
            Assert.That(imported.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(imported.Data.ExecutionWitness, Is.Not.Null);
            Assert.That(regenerated.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(regenerated.Data.ExecutionWitness, Is.Not.Null, "the known block's witness is generated again");
            Assert.That(afterProduction, Is.Zero, "the payload builder must not run on a query module's runner");
            Assert.That(afterImport, Is.Zero, "engine_newPayloadWithWitness must not run on a query module's runner");
            Assert.That(afterRegeneration, Is.Zero, "the witness re-execution must not run on a query module's runner");
            Assert.That(call.Error, Is.Null);
            Assert.That(probe.Offers, Is.GreaterThan(0), "precondition: the module is loaded into the query environments");
        }
    }
}
