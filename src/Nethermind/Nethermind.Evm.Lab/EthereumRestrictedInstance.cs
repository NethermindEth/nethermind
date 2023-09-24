// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

public class EthereumRestrictedInstance : VirtualMachineTestsBase
{
    private ReleaseSpec ActivatedSpec { get; }

    public EthereumRestrictedInstance(IReleaseSpec activatedSpec)
        : base()
    {
        ActivatedSpec = (ReleaseSpec)activatedSpec;
        SpecProvider = new TestSpecProvider(Frontier.Instance, ActivatedSpec);
        Setup();
    }

    public void UpdateStorageCell(StorageCell cell, Byte[] value, bool isTransient)
    {
        if(!isTransient)
        {
            this.TestState.Set(cell, value);
        } else
        {
            this.TestState.SetTransientState(cell, value);
        }
    }

    public T ExecuteBytecode<T>(T tracer, long gas, params byte[] code) where T : ITxTracer
        => Execute<T>(tracer, Math.Min(VirtualMachineTestsBase.DefaultBlockGasLimit, gas), code);
}
