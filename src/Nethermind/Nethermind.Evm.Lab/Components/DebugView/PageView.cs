// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using DebuggerStateEvents;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components.GlobalViews;
using Nethermind.Evm.Lab.Components.TracerView;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.Debugger;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.DebugView;

// Note(Ayman) : Add possibility to run multiple bytecodes at once using tabular views
internal class DebuggerView : IComponent<DebuggerState>
{
    public record Components(
        MachineDataView MachineOverview, StackView StackComponent, MemoryView RamComponent, InputsView InputComponent,
        StorageView StorageComponent, BytecodeView ProgramComponent, ConfigsView ConfigComponent, MediaLikeView ControlsComponent,
        ConditionView ConditionComponent, FooterView FooterComponent, StacktraceView StacktraceComponent
    ) : IDisposable
    {
        public void Dispose()
        {
            MachineOverview?.Dispose();
            StackComponent?.Dispose();
            RamComponent?.Dispose();
            InputComponent?.Dispose();
            StorageComponent?.Dispose();
            ProgramComponent?.Dispose();
            ConfigComponent?.Dispose();
            ControlsComponent?.Dispose();
            ConditionComponent?.Dispose();
            FooterComponent?.Dispose();
            StacktraceComponent?.Dispose();
        }
    }
    private View MainPanel;
    public DebuggerState? defaultValue;
    public bool isCached = false;
    public Components _components;
    public DebuggerView()
    {
        _components = new Components(
            new MachineDataView(),
            new StackView(),
            new MemoryView(),
            new InputsView(),
            new StorageView(),
            new BytecodeView(),
            new ConfigsView(),
            new MediaLikeView(),
            new ConditionView(),
            new FooterView(),
            new StacktraceView()
        );
    }



    public (View, Rectangle?) View(DebuggerState state, Rectangle? rect = null)
    {
        var _component_cpu = _components.MachineOverview;
        var _component_stk = _components.StackComponent;
        var _component_ram = _components.RamComponent;
        var _component_inpt = _components.InputComponent;
        var _component_strg = _components.StorageComponent;
        var _component_pgr = _components.ProgramComponent;
        var _component_cnfg = _components.ConfigComponent;
        var _component_cntrl = _components.ControlsComponent;
        var _component_check = _components.ConditionComponent;
        var _component_foot = _components.FooterComponent;
        var _component_strace = _components.StacktraceComponent;

        GethTxTraceEntry currentEntry = state.Tracer.GetCurrentEntry();

        MainPanel ??= new View()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var (cpu_view, cpu_rect) = _component_cpu.View(currentEntry, new Rectangle(0, 0, Dim.Percent(30), Dim.Percent(25))); // h:10 w:30
        var (stack_view, stack_rect) = _component_stk.View((state.Tracer?.CurrentState?.DataStack ?? Array.Empty<byte>(), state.Tracer?.CurrentState?.DataStackHead ?? 0, state.Tracer?.CurrentState is null), cpu_rect.Value with // h:50 w:30
        {
            X = 0,
            Y = Pos.Bottom(cpu_view),
            Height = Dim.Percent(40)
        });


        var (stacktrace_view, stacktrace_rect) = _component_strace.View(state.Tracer, stack_rect.Value with // h: 100, w:100
        {
            Y = Pos.Bottom(stack_view),
            Width = Dim.Percent(30),
            Height = Dim.Percent(30)
        });

        var memoryState = state.Tracer?.CurrentState?.Memory?.Load(0, (UInt256)(state.Tracer?.CurrentState?.Memory?.Size ?? 0)).ToArray() ?? Array.Empty<byte>();
        var (ram_view, ram_rect) = _component_ram.View((memoryState, true), stack_rect.Value with // h: 100, w:100
        {
            X = Pos.Right(stacktrace_view),
            Y = Pos.Bottom(stack_view),
            Width = Dim.Fill(),
            Height = Dim.Fill()
        });

        var (footer_view, footer_rect) = _component_foot.View(state?.Tracer.CurrentState, stacktrace_rect.Value with
        {
            Y = Pos.Bottom(stacktrace_view),
            Width = Dim.Width(stacktrace_view),
            Height = Dim.Fill()
        });

        var (input_view, input_rect) = _component_inpt.View((state.RuntimeContext, Array.Empty<byte>(), state.SelectedFork), cpu_rect.Value with // h: 10, w : 80
        {
            X = Pos.Right(cpu_view),
            Width = Dim.Percent(50)
        });
        var (storage_view, storage_rect) = _component_strg.View(currentEntry?.Storage ?? new Dictionary<string, string>(), input_rect.Value with // h: 40, w: 80
        {
            Y = Pos.Bottom(input_view),
            Height = Dim.Percent(50),
        });
        var (config_view, config_rect) = _component_cnfg.View((state.SelectedFork, state.AvailableGas), input_rect.Value with
        {
            X = Pos.Right(input_view),
            Width = Dim.Percent(20),
            Height = Dim.Percent(20)
        });

        var (condition_view, condition_rect) = _component_check.View(config_rect.Value with
        {
            Y = Pos.Bottom(config_view),
            Height = Dim.Percent(7),
        });

        var (program_view, program_rect) = _component_pgr.View((state.Tracer, state.RuntimeContext, state.SelectedFork), condition_rect.Value with
        {
            Y = Pos.Bottom(condition_view),
            Height = Dim.Percent(33),
        });
        var (controls_view, controls_rect) = _component_cntrl.View((state.Tracer.CurrentPhase is not DebugTracer.DebugPhase.Starting, state.Tracer.CanReadState), program_rect.Value with
        {
            Y = Pos.Bottom(program_view),
            Height = Dim.Percent(7),
        });


        cpu_view.Enabled = stack_view.Enabled = ram_view.Enabled = storage_view.Enabled = stacktrace_view.Enabled = footer_view.Enabled = state.IsActive;
        input_view.Enabled = config_view.Enabled = condition_view.Enabled = program_view.Enabled = controls_view.Enabled = true;


        if (!isCached)
        {
            _component_ram.ByteEdited += (idx, newValue) =>
            {
                state.Tracer.CurrentState.Memory.SaveByte((UInt256)idx, newValue);
                state.EventsSink.EnqueueEvent(new Update(), true);
            };
            _component_stk.ByteEdited += (idx, newValue) =>
            {
                state.Tracer.CurrentState.DataStack[idx] = newValue;
                state.EventsSink.EnqueueEvent(new Update(), true);
            };
            _component_stk.StackHeightChangeRequest += (heightDelta) =>
            {
                state.Tracer.CurrentState.DataStackHead += heightDelta;
                state.EventsSink.EnqueueEvent(new Update(), true);
            };

            HookKeyboardEvents(state);
            _component_cntrl.ActionRequested += e => FireEvent(state, e);
            _component_cnfg.ConfigsChanged += e => FireEvent(state, e);
            _component_inpt.InputChanged += e => FireEvent(state, e);
            _component_pgr.BreakPointRequested += (e) => FireEvent(state, e);
            _component_check.ActionRequested += (e) => FireEvent(state, e);
            _component_stk.EventRequested += (e) => FireEvent(state, e);

            MainPanel.Add(program_view, config_view, storage_view, input_view, ram_view, stack_view, cpu_view, controls_view, condition_view, footer_view, stacktrace_view);
        }
        isCached = true;
        return (MainPanel, null);
    }

    private void FireEvent(DebuggerState state, ActionsBase eventMessage) => state.EventsSink.EnqueueEvent(eventMessage, true);

    private void HookKeyboardEvents(DebuggerState state)
    {
        MainPanel.KeyUp += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.F:
                    FireEvent(state, new MoveNext(true));
                    break;

                case Key.N:
                    FireEvent(state, new MoveNext(false));
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
