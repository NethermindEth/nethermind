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
    private GethLikeTxTracer _tracer => new(GethTraceOptions.Default);
    private PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    private Window MainPanel = new Window("EvmLaboratory");
    public MachineState InitialState;

    public bool isCached = false;
    public IComponent<MachineState>[] _components;
    public MainView(string pathOrBytecode)
    {
        byte[] bytecode = Core.Extensions.Bytes.FromHexString(Uri.IsWellFormedUriString(pathOrBytecode, UriKind.Absolute) ? File.OpenText(pathOrBytecode).ReadToEnd() : pathOrBytecode);

        var resultTraces = context.Execute(_tracer, long.MaxValue, bytecode);
        InitialState = new MachineState()
        {
            Bytecode = bytecode,
            CallData = Array.Empty<byte>(),
        };
        EventsSink.EnqueueEvent(new UpdateState(resultTraces.BuildResult()));

        _components = new IComponent<MachineState>[]{
            new HeaderView(),
            new MachineOverview(),
            new StackView(),
            new MemoryView(),
            new InputsView(),
            new ReturnView(),
            new StorageView(),
            new ProgramView()
        };
    }

    public void Run(MachineState _)
    {
        bool firstRender = true;

        HookKeyboardEvents();
        Application.Init();
        Application.MainLoop.Invoke(
            async () =>
            {
                do
                {
                    if (EventsSink.TryDequeueEvent(out var currentEvent))
                    {
                        lock (InitialState)
                        {
                            InitialState = Update(InitialState, currentEvent).GetState();
                            if (firstRender)
                            {
                                Application.Top.Add(_components[0].View(InitialState).Item1, View(InitialState).Item1);
                                firstRender = false;
                            }
                            else View(InitialState);
                        }
                    }
                }
                while (firstRender || await timer.WaitForNextTickAsync());
            });

        Application.Run();
        Application.Shutdown();
    }
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        IComponent<MachineState> _component_cpu = _components[1];
        IComponent<MachineState> _component_stk = _components[2];
        IComponent<MachineState> _component_ram = _components[3];
        IComponent<MachineState> _component_inpt = _components[4];
        IComponent<MachineState> _component_rtrn = _components[5];
        IComponent<MachineState> _component_strg = _components[6];
        IComponent<MachineState> _component_pgr = _components[7];

        var (view1, rect1) = _component_cpu.View(state, new Rectangle(0, 0, Dim.Percent(30), 10));
        var (view2, rect2) = _component_stk.View(state, rect1.Value with
        {
            Y = Pos.Bottom(view1),
            Height = Dim.Percent(45)
        });
        var (view3, rect3) = _component_ram.View(state, rect2.Value with
        {
            Y = Pos.Bottom(view2),
            Width = Dim.Fill()
        });
        var (view4, rect4) = _component_inpt.View(state, rect1.Value with
        {
            X = Pos.Right(view1),
            Width = Dim.Percent(50)
        });
        var (view5, rect5) = _component_strg.View(state, rect4.Value with
        {
            Y = Pos.Bottom(view4),
            Width = Dim.Percent(50),
            Height = Dim.Percent(25),
        });
        var (view6, rect6) = _component_rtrn.View(state, rect4.Value with
        {
            Y = Pos.Bottom(view5),
            Height = Dim.Percent(20),
            Width = Dim.Percent(50)
        });
        var (view7, rect7) = _component_pgr.View(state, rect4.Value with
        {
            X = Pos.Right(view4),
            Height = Dim.Percent(65),
            Width = Dim.Percent(20)
        });

        if (!isCached)
            MainPanel.Add(view1, view4, view2, view3, view5, view6, view7);
        isCached = true;
        return (MainPanel, null);
    }

    public IState<MachineState> Update(IState<MachineState> state, ActionsBase msg)
    {
        static void ShowError(string mesg)
        {
            var cancel = new Button(10, 14, "OK");
            cancel.Clicked += () => Application.RequestStop();
            var dialog = new Dialog("Error", 60, 7, cancel);

            var entry = new Label(mesg)
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

                    EventsSink.EnqueueEvent(new BytecodeInserted(file.ReadToEnd()), true);

                    break;
                }
            case BytecodeInserted biMsg:
                {
                    try
                    {
                        var bytecode = Nethermind.Core.Extensions.Bytes.FromHexString(biMsg.bytecode);
                        state.GetState().Bytecode = bytecode;
                        var localTracer = _tracer;
                        context.Execute(localTracer, long.MaxValue, bytecode);
                        EventsSink.EnqueueEvent(new UpdateState(localTracer.BuildResult()), true);
                    }
                    catch
                    {
                        ShowError("bytecode ill-formated");
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
                        ShowError("calldata ill-formated");
                        state.GetState().CallData = Array.Empty<byte>();
                    }
                    break;
                }
            case UpdateState updState:
                return state.GetState().SetState(updState.traces);
        }
        return state;
    }

    private void HookKeyboardEvents()
    {
        MainPanel.KeyUp += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.F:
                    EventsSink.EnqueueEvent(new MoveNext());
                    break;

                case Key.B:
                    EventsSink.EnqueueEvent(new MoveBack());
                    break;
            }

        };
    }
}
