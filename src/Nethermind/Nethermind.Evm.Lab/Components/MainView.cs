// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using GlobalStateEvents.Actions;
using Nethermind.Evm.Lab.Components.DebugView;
using Nethermind.Evm.Lab.Components.Differ;
using Nethermind.Evm.Lab.Components.GlobalViews;
using Nethermind.Evm.Lab.Components.TracerView;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components;

// Note(Ayman) : Add possibility to run multiple bytecodes at once using tabular views
internal class MainView : IComponent<GlobalState>
{
    private List<IComponentObject> pages = new();
    private bool isCached;
    private Window container;
    private TabView table;
    private HeaderView header;
    private PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
    public GlobalState State = new GlobalState();
    public MainView()
    {
        header = new HeaderView();
    }

    private void AddMachinePage(MachineState? state = null, string name = null, bool isExternalTrace = false) // if name is null it means this is a internal viz
    {
        var pageObj = new MachineView(isExternalTrace);
        var pageState = state ?? new MachineState();
        pages.Add(pageObj);
        State.MachineStates.Add(pageState);
        if (state is null)
        {
            pageState.Initialize(true);
        }
        var tab = new TabView.Tab(name ?? "Default", pageObj.View(pageState).Item1);
        table.AddTab(tab, true);
    }

    private void AddTracesPage(TraceState state = null, string name = null)
    {
        var pageObj = new TracesView();
        var pageState = state ?? new TraceState();
        pages.Add(pageObj);
        State.MachineStates.Add(pageState);
        var tab = new TabView.Tab(name ?? "Default", pageObj.View(pageState).Item1);
        table.AddTab(tab, true);
    }
    private void AddDebugPage(DebuggerState state = null, string name = null)
    {
        var pageObj = new DebuggerView();
        var pageState = state ?? new DebuggerState();
        pages.Add(pageObj);
        State.MachineStates.Add(pageState);
        if (state is null)
        {
            pageState.Initialize();
        }
        var tab = new TabView.Tab(name ?? "Default", pageObj.View(pageState).Item1);
        table.AddTab(tab, true);
    }

    private void RemoveMachinePage(int idx)
    {
        int index = 0;
        if (State.MachineStates.Count > 1)
        {
            TabView.Tab targetTab = null;
            foreach (var tab in table.Tabs)
            {
                if (idx == index)
                {
                    targetTab = tab;
                    break;
                }
                index++;
            }
            table.RemoveTab(targetTab);
            var page = pages[index];
            pages.RemoveAt(index);
            State.MachineStates.RemoveAt(index);
            page.Dispose();
        }
    }

    private int GetTabIndex(TabView.Tab page)
    {
        int index = 0;
        foreach (var tab in table.Tabs)
        {
            if (tab == page)
            {
                return index;
            }
            index++;
        }
        throw new UnreachableException();
    }

    private void UpdateTabPage(View page, int idx)
    {
        int index = 0;
        foreach (var tab in table.Tabs)
        {
            if (idx == index)
            {
                tab.View = page;
                break;
            }
        }

    }

    public static Dialog ShowError(string mesg, Action? cancelHandler = null)
    {
        var cancel = new Button("OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(5)
        };

        if (cancelHandler is not null)
            cancel.Clicked += cancelHandler;

        var dialog = new Dialog("Error", 60, 7, cancel)
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(25),
            Height = Dim.Percent(25),
        };
        cancel.Clicked += () => dialog.RequestStop();

        var entry = new TextView()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Percent(50),
            Enabled = false,
            Text = mesg,
            WordWrap = true,
        };
        dialog.Add(entry);
        Application.Run(dialog);
        return dialog;
    }

    public async Task<bool> MoveNext(GlobalState state)
    {
        if (state.EventsSink.TryDequeueEvent(out var currentEvent))
        {
            lock (state)
            {
                try
                {
                    state = Update(state, currentEvent);
                }
                catch (Exception ex)
                {
                    var dialogView = MainView.ShowError(ex.Message,
                        () =>
                        {
                            state.EventsSink.EnqueueEvent(new Reset());
                        }
                    );
                }
            }
            return true;
        }
        return false;
    }

    public async Task Run(GlobalState state)
    {
        bool firstRender = true;
        Application.Init();
        Application.Top.Add(header.View(state).Item1, View(state).Item1);
        Application.MainLoop.Invoke(
            async () =>
            {
                if (firstRender)
                {
                    firstRender = false;
                    Application.Top.Add(header.View(state).Item1, View(state).Item1);
                }

                do
                {

                    await MoveNext(state);
                    for (int i = 0; i < pages.Count; i++)
                    {
                        if (await State.MachineStates[i].MoveNext() && i == state.SelectedState)
                        {
                            switch (pages[i])
                            {
                                case MachineView machineView:
                                    UpdateTabPage(machineView.View(State.MachineStates[i] as MachineState).Item1, i);
                                    break;
                                case TracesView traceView:
                                    _ = traceView.View(State.MachineStates[i] as TraceState).Item1;
                                    break;
                                case DebuggerView debugView:
                                    _ = debugView.View(State.MachineStates[i] as DebuggerState).Item1;
                                    break;
                            }
                        }
                    }
                }
                while (firstRender || await timer.WaitForNextTickAsync());
            });
        Application.Run();
        Application.Shutdown();
    }
    public (View, Rectangle?) View(GlobalState innerState, Rectangle? rect = null)
    {
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? Dim.Fill(),
                Height: rect?.Height ?? Dim.Fill()
            );

        container ??= new Window("EvmLaboratory")
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = Colors.TopLevel
        };

        table ??= new TabView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        if (!isCached)
        {
            table.SelectedTabChanged += (s, e) => innerState.SelectedState = GetTabIndex(e.NewTab);
            AddMachinePage();
            container.Add(table);
            isCached = true;
        }
        return (container, frameBoundaries);
    }

    public GlobalState Update(GlobalState state, ActionsBase msg)
    {
        switch (msg)
        {
            case AddPage<MachineState> msgA:
                {
                    AddMachinePage(msgA.customState, name: msgA.name, msgA.isExternalTrace);
                    break;
                }
            case AddPage<TraceState> msgA:
                {
                    AddTracesPage(msgA.customState, name: msgA.name);
                    break;
                }
            case AddPage<DebuggerState> msgA:
                {
                    AddDebugPage(msgA.customState, name: msgA.name);
                    break;
                }
            case RemovePage msgR:
                {
                    RemoveMachinePage(state.SelectedState);
                    break;
                }
        }

        return state;
    }

    public void Dispose()
    {
        container?.Dispose();
        table?.Dispose();
        header?.Dispose();
    }
}
