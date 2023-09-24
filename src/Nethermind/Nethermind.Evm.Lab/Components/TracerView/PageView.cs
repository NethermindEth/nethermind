// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components.GlobalViews;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;

// Note(Ayman) : Add possibility to run multiple bytecodes at once using tabular views
internal class MachineView : IComponent<MachineState>
{
    public record Components(
        MachineDataView MachineOverview, StackView StackComponent, MemoryView RamComponent, ReturnView ReturnComponent,
        InputsView InputComponent, StorageView StorageComponent, ProgramView ProgramComponent, ConfigsView ConfigComponent,
        MediaLikeView ControlsComponent
    ) : IDisposable
    {
        public void Dispose()
        {
            MachineOverview?.Dispose();
            StackComponent?.Dispose();
            RamComponent?.Dispose();
            InputComponent?.Dispose();
            ReturnComponent?.Dispose();
            StorageComponent?.Dispose();
            ProgramComponent?.Dispose();
            ConfigComponent?.Dispose();
            ControlsComponent?.Dispose();
        }
    }
    private View MainPanel;
    public MachineState? defaultValue;
    private bool isExternalTraceVizView = false;
    public bool isCached = false;
    public Components _components;
    public MachineView(bool isExternalTraceViz)
    {
        isExternalTraceVizView |= isExternalTraceViz;
        _components = new Components(
            new MachineDataView(),
            new StackView(),
            new MemoryView(),
            new ReturnView(),
            new InputsView(),
            new StorageView(),
            new ProgramView(),
            new ConfigsView(),
            new MediaLikeView()
        );
    }



    public (View, Rectangle?) View(MachineState state, Rectangle? rect = null)
    {
        var _component_cpu = _components.MachineOverview;
        var _component_stk = _components.StackComponent;
        var _component_ram = _components.RamComponent;
        var _component_inpt = _components.InputComponent;
        var _component_rtrn = _components.ReturnComponent;
        var _component_strg = _components.StorageComponent;
        var _component_pgr = _components.ProgramComponent;
        var _component_cnfg = _components.ConfigComponent;
        var _component_cntrl = _components.ControlsComponent;

        MainPanel ??= new View()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var (cpu_view, cpu_rect) = _component_cpu.View((state.Current), new Rectangle(0, 0, Dim.Percent(30), Dim.Percent(25))); // h:10 w:30
        var (stack_view, stack_rect) = _component_stk.View((state.Current.Stack.Select(entryStr => Bytes.FromHexString(entryStr)).ToArray()), cpu_rect.Value with // h:50 w:30
        {
            Y = Pos.Bottom(cpu_view),
            Height = Dim.Percent(40)
        });

        var memoryState = Core.Extensions.Bytes.FromHexString(String.Join(string.Empty, state.Current.Memory?.Select(row => row.Replace("0x", String.Empty)) ?? new List<string>()));
        var (ram_view, ram_rect) = _component_ram.View((memoryState, false), stack_rect.Value with // h: 100, w:100
        {
            Y = Pos.Bottom(stack_view),
            Width = Dim.Fill(),
            Height = Dim.Fill()
        });
        var (input_view, input_rect) = _component_inpt.View((state.RuntimeContext, state.CallData, state.SelectedFork), cpu_rect.Value with // h: 10, w : 80
        {
            X = Pos.Right(cpu_view),
            Width = Dim.Percent(50)
        });
        var (storage_view, storage_rect) = _component_strg.View(state.Current.Storage, input_rect.Value with // h: 40, w: 80
        {
            Y = Pos.Bottom(input_view),
            Height = Dim.Percent(30),
        });
        var (return_view, return_rect) = _component_rtrn.View(state.ReturnValue, storage_rect.Value with
        {
            Y = Pos.Bottom(storage_view),
            Height = Dim.Percent(10),
        });
        var (config_view, config_rect) = _component_cnfg.View((state.SelectedFork, state.AvailableGas), input_rect.Value with
        {
            X = Pos.Right(input_view),
            Width = Dim.Percent(20)
        });
        var (program_view, program_rect) = _component_pgr.View(state, config_rect.Value with
        {
            Y = Pos.Bottom(config_view),
            Height = Dim.Percent(33),
        });
        var (controls_view, controls_rect) = _component_cntrl.View(program_rect.Value with
        {
            Y = Pos.Bottom(program_view),
            Height = Dim.Percent(7),
        });

        input_view.Enabled = !isExternalTraceVizView;
        config_view.Enabled = !isExternalTraceVizView;

        if (!isCached)
        {
            HookKeyboardEvents(state);
            _component_cntrl.ActionRequested += e => FireEvent(state, e);
            _component_cnfg.ConfigsChanged += e => FireEvent(state, e);
            _component_inpt.InputChanged += e => FireEvent(state, e);
            MainPanel.Add(program_view, config_view, return_view, storage_view, input_view, ram_view, stack_view, cpu_view, controls_view);
        }
        isCached = true;
        return (MainPanel, null);
    }

    private void FireEvent(MachineState state, ActionsBase eventMessage) => state.EventsSink.EnqueueEvent(eventMessage);

    private void HookKeyboardEvents(MachineState state)
    {
        MainPanel.KeyUp += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.F:
                    FireEvent(state, new MoveNext());
                    break;

                case Key.B:
                    FireEvent(state, new MoveBack());
                    break;
            }

        };
    }

    public void Dispose()
    {
        MainPanel?.Dispose();
        _components?.Dispose();
    }
}
