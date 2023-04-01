// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Componants;
using Nethermind.Evm.Lab.Components.GlobalViews;
using Nethermind.Evm.Lab.Components.MachineLab;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Test;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Terminal.Gui;
using State = Nethermind.Evm.Lab.MachineState;

EthereumRestrictedInstance context = new(Cancun.Instance);
GethLikeTxTracer _tracer = new(GethTraceOptions.Default);

var resultTraces = context.Execute(_tracer, long.MaxValue, Nethermind.Core.Extensions.Bytes.FromHexString("0x604260005260206000F3"));

var state = new State(resultTraces.BuildResult()).Goto(5);
var rng = new Random(0);

state.Bytecode = "0x604260005260206000F3";
for (int i = 0; i < 10; i++)
{
    state.Current.Storage[$"0x{rng.Next():X4}"] = $"0x{rng.Next():X4}";

}

IComponent<State> _component_hdr = new HeaderView();
IComponent<State> _component_cpu = new MachineOverview();
IComponent<State> _component_stk = new StackView();
IComponent<State> _component_ram = new MemoryView();
IComponent<State> _component_inpt = new InputsView();
IComponent<State> _component_rtrn = new ReturnView();
IComponent<State> _component_strg = new StorageView();
IComponent<State> _component_pgr = new ProgramView();

var window = new Window("EvmLaboratory");

var (view1, rekt1) = _component_cpu.View(state, new Rectangle(0, 0, Dim.Percent(30), 10));
var (view2, rekt2) = _component_stk.View(state, rekt1.Value with
{
    Y = Pos.Bottom(view1),
    Height = Dim.Percent(45)
});
var (view3, rekt3) = _component_ram.View(state, rekt2.Value with
{
    Y = Pos.Bottom(view2),
    Width = Dim.Fill()
});
var (view4, rekt4) = _component_inpt.View(state, rekt1.Value with
{
    X = Pos.Right(view1),
    Width = Dim.Percent(50)
});
var (view5, rekt5) = _component_strg.View(state, rekt4.Value with
{
    Y = Pos.Bottom(view4),
    Width = Dim.Percent(50),
    Height = Dim.Percent(25),
});
var (view6, rekt6) = _component_rtrn.View(state, rekt4.Value with
{
    Y = Pos.Bottom(view5),
    Height = Dim.Percent(20),
    Width = Dim.Percent(50)
});
var (view7, rekt7) = _component_pgr.View(state, rekt4.Value with
{
    X = Pos.Right(view4),
    Height = Dim.Percent(65),
    Width = Dim.Percent(20)
});

window.Add(view1);
window.Add(view4);
window.Add(view2);
window.Add(view3);
window.Add(view5);
window.Add(view6);
window.Add(view7);

Application.Init();
Application.Top.Add(
    _component_hdr.View(state).Item1, window
); ;
Application.Run();
Application.Shutdown();


public class EthereumRestrictedInstance
{

    private VirtualMachineTestsBase _runningContext;
    private ReleaseSpec ActivatedSpec { get; }
    public EthereumRestrictedInstance(int[] activatedEips)
    {
        ActivatedSpec = new ReleaseSpec();
        _runningContext = new VirtualMachineTestsBase();
        var properties = typeof(ReleaseSpec).GetProperties()
            .Where(prop => activatedEips.Any(eip => prop.Name == $"IsEip{eip}Enabled"));
        foreach (PropertyInfo property in properties)
        {
            property.SetValue(ActivatedSpec, true);
        }
        _runningContext.SpecProvider = new TestSpecProvider(Frontier.Instance, ActivatedSpec);
        _runningContext.Setup();
    }

    public EthereumRestrictedInstance(IReleaseSpec activatedSpec)
    {
        _runningContext = new VirtualMachineTestsBase();
        ActivatedSpec = (ReleaseSpec)activatedSpec;
        _runningContext.SpecProvider = new TestSpecProvider(Frontier.Instance, ActivatedSpec);
        _runningContext.Setup();
    }

    public T Execute<T>(T tracer, long gas, params byte[] code) where T : ITxTracer
        => _runningContext.Execute<T>(tracer, code);
}
