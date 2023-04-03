// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Components;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;


var mainView = new MainView(args.Length == 0 ? "604260005260206000F3" : args[0]);
mainView.Run(mainView.InitialState);




public class EthereumRestrictedInstance
{

    private VirtualMachineTestsBase _runningContext;
    private ReleaseSpec ActivatedSpec { get; }

    public EthereumRestrictedInstance(IReleaseSpec activatedSpec)
    {
        _runningContext = new VirtualMachineTestsBase();
        ActivatedSpec = (ReleaseSpec)activatedSpec;
        _runningContext.SpecProvider = new TestSpecProvider(Frontier.Instance, ActivatedSpec);
        _runningContext.Setup();
    }

    public T Execute<T>(T tracer, long gas, params byte[] code) where T : ITxTracer
        => _runningContext.Execute<T>(tracer, Math.Min(VirtualMachineTestsBase.DefaultBlockGasLimit, gas), code);
}
