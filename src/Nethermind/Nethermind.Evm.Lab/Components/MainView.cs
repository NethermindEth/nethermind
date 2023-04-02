// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineState.Actions;
using Nethermind.Evm.Lab.Componants;
using Nethermind.Evm.Lab.Components.GlobalViews;
using Nethermind.Evm.Lab.Components.MachineLab;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Specs.Forks;
using Terminal.Gui;
namespace Nethermind.Evm.Lab.Components;
internal class MainView : IComponent<MachineState>
{
    private EthereumRestrictedInstance context = new(Cancun.Instance);
    private GethLikeTxTracer _tracer = new(GethTraceOptions.Default);
    public MachineState InitialState;
    public MainView(string pathOrBytecode)
    {
        byte[] bytecode = Core.Extensions.Bytes.FromHexString(Uri.IsWellFormedUriString(pathOrBytecode, UriKind.Absolute) ? File.OpenText(pathOrBytecode).ReadToEnd() : pathOrBytecode);

        var resultTraces = context.Execute(_tracer, long.MaxValue, bytecode);
        InitialState = new MachineState(resultTraces.BuildResult())
        {
            Bytecode = bytecode,
            CallData = Array.Empty<byte>(),
        };
    }

    public void Run(MachineState state)
    {
        Application.Init();
        View(state);

        Application.MainLoop.Invoke(
            async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromMicroseconds(1000));
                while (await timer.WaitForNextTickAsync())
                {
                    if (state.TryDequeueEvent(out var currentEvent))
                    {
                        var newState = Update(state, currentEvent);
                        if (newState != null)
                        {
                            View(newState);
                        }
                    }
                }
                Application.Refresh();
            });

        Application.Run();
        Application.Shutdown();
    }
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        Application.Top.Clear();
        IComponent<MachineState> _component_hdr = new HeaderView();
        IComponent<MachineState> _component_cpu = new MachineOverview();
        IComponent<MachineState> _component_stk = new StackView();
        IComponent<MachineState> _component_ram = new MemoryView();
        IComponent<MachineState> _component_inpt = new InputsView();
        IComponent<MachineState> _component_rtrn = new ReturnView();
        IComponent<MachineState> _component_strg = new StorageView();
        IComponent<MachineState> _component_pgr = new ProgramView();

        var MainPanel = new Window("EvmLaboratory");

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

        MainPanel.Add(view1);
        MainPanel.Add(view4);
        MainPanel.Add(view2);
        MainPanel.Add(view3);
        MainPanel.Add(view5);
        MainPanel.Add(view6);
        MainPanel.Add(view7);

        MainPanel.KeyUp += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.Space:
                    state.GetState().EnqueueEvent(new MoveNext());
                    break;

                case Key.Backspace:
                    state.GetState().EnqueueEvent(new MoveBack());
                    break;
            }

        };

        Application.Top.Add(
            _component_hdr.View(state).Item1, MainPanel
        ); ;
        return (Application.Top, null);
    }

    public IState<MachineState> Update(IState<MachineState> state, ActionsBase msg)
    {
        static void ShowError(string mesg)
        {
            var cancel = new Button(10, 14, "OK");
            cancel.Clicked += () => Application.RequestStop();
            var dialog = new Dialog("Error", 60, 7, cancel);

            var entry = new TextField()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(),
                Height = 1
            };
            dialog.Add(entry);
        }

        switch (msg)
        {
            case MoveNext _:
                return state.GetState().Next();
            case MoveBack _:
                return state.GetState().Previous();
            case FileLoaded flMsg:
                {
                    var file = File.OpenText(flMsg.filePath);
                    if (file == null)
                    {
                        ShowError("File Not found");
                    }

                    state.GetState().EnqueueEvent(new BytecodeInserted(file.ReadToEnd()));

                    break;
                }
            case BytecodeInserted biMsg:
                {
                    try
                    {
                        var bytecode = Nethermind.Core.Extensions.Bytes.FromHexString(biMsg.bytecode);
                        state.GetState().Bytecode = bytecode;
                        context.Execute(_tracer, long.MaxValue, bytecode);
                    }
                    catch
                    {
                        state.GetState().Bytecode = Array.Empty<byte>();
                    }
                    break;
                }
            case CallDataInserted ciMsg:
                {
                    try
                    {
                        var calldata = Nethermind.Core.Extensions.Bytes.FromHexString(ciMsg.calldata);
                        state.GetState().CallData = calldata;
                    }
                    catch
                    {
                        state.GetState().CallData = Array.Empty<byte>();
                    }
                    break;
                }
        }
        return state;
    }
}
