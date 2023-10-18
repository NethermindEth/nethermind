// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

public class EthereumRestrictedInstance
{
    public VirtualMachineTestsBase TestBackend;
    public INethermindApi NethermindApi;
    public ReleaseSpec ActivatedSpec { get; set; }

    public EthereumRestrictedInstance(IReleaseSpec activatedSpec)
    {
        TestBackend = new VirtualMachineTestsBase();
        ActivatedSpec = (ReleaseSpec)activatedSpec;
        TestBackend.SpecProvider = new TestSpecProvider(Frontier.Instance, ActivatedSpec);
        TestBackend.Setup();
    }

    public EthereumRestrictedInstance(INethermindApi api)
        => NethermindApi = api;

    public T ExecuteBytecode<T>(T tracer, long gas, params byte[] code) where T : ITxTracer
        => TestBackend.Execute<T>(tracer, Math.Min(VirtualMachineTestsBase.DefaultBlockGasLimit, gas), code);
    public T ExecuteTransaction<T>(T tracer, Transaction tx, BlockParameter param) where T : ITxTracer
    {
        BlockHeader header = NethermindApi.BlockTree.FindBlock(param)!.Header;
        NethermindApi.TransactionProcessor.Execute(tx, (BlockExecutionContext)header, tracer);
        return tracer;
    }
}
