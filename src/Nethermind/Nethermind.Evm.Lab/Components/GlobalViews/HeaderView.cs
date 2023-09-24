// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using GlobalStateEvents.Actions;
using MachineStateEvents;
using Nethermind.Consensus.Tracing;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;
using Newtonsoft.Json;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.GlobalViews;
internal class HeaderView : IComponent<GlobalState>
{
    private bool IsCached = false;
    private MenuBar menu;

    public void Dispose()
    {
        menu?.Dispose();
    }

    public (View, Rectangle?) View(GlobalState state, Rectangle? rect = null)
    {
        if (!IsCached)
        {
            menu ??= new MenuBar(new MenuBarItem[] {
                new MenuBarItem ("_File", new MenuItem [] {
                    new MenuItem ("_Run", "", () => {
                        using var fileOpenDialogue = new OpenDialog("Bytecode File", "Select a binary file that contains EVM bytecode");
                        Application.Run(fileOpenDialogue);
                        if(fileOpenDialogue.Canceled) return;
                        var filePath = (string)fileOpenDialogue.FilePath;
                        var contentAsText = File.ReadAllText (filePath);
                        if(state.MachineStates[state.SelectedState] is MachineState substate)
                        {
                            substate.EventsSink.EnqueueEvent(new BytecodeInserted(contentAsText));
                        } else
                        {
                            MainView.ShowError("Selected View Must be a MachineView");
                        }
                    }),
                    new MenuItem ("_Open", "", () => {
                        using var fileOpenDialogue = new OpenDialog("Trace File", "Select a Traces file that contains EVM trace",  new List<string> { ".json" });
                        Application.Run(fileOpenDialogue);
                        if(fileOpenDialogue.Canceled) return;
                        var filePath = (string)fileOpenDialogue.FilePath;
                        var fileName = Path.GetFileNameWithoutExtension (filePath);
                        var contentAsText = File.ReadAllText (filePath);
                        try {
                            GethLikeTxTrace? traces = JsonConvert.DeserializeObject<GethLikeTxTrace>(contentAsText);
                            if(traces is not null && traces.Entries.Count > 0)
                            {
                                state.EventsSink.EnqueueEvent(new AddPage<MachineState>(fileName, new MachineState(traces), isExternalTrace: true));
                                return;
                            }
                            else goto  error_section;
                        } catch
                        {
                            goto  error_section;
                        }
error_section:              MainView.ShowError("Failed to deserialize Traces Provided!");
                    }),
                    new MenuItem ("_Export", "", () => {
                        // open trace file
                        using var saveOpenDialogue = new SaveDialog("Bytecode File", "Select a binary file that contains EVM bytecode");
                        Application.Run(saveOpenDialogue);
                        if(saveOpenDialogue.Canceled) return;
                        var filePath = (string)saveOpenDialogue.FilePath;
                        if(state.MachineStates[state.SelectedState] is MachineState substate)
                        {
                            var serializedData = JsonConvert.SerializeObject(substate as GethLikeTxTrace);
                            File.WriteAllText($"{filePath}.json", serializedData);
                        } else
                        {
                            MainView.ShowError("Selected View Must be a MachineView");
                        }
                    }),
                    new MenuItem ("_Quit", "", () => {
                        Application.RequestStop ();
                    })
                }),

                new MenuBarItem ("_Action", new MenuItem [] {
                    new MenuItem ("_New", "", () => {
                        state.EventsSink.EnqueueEvent(new AddPage<MachineState>($"Page {state.MachineStates.Count}", isExternalTrace: false));
                    }),
                    new MenuItem ("_Remove", "", () => {
                        state.EventsSink.EnqueueEvent(new RemovePage(state.SelectedState));
                    }),
                    new MenuItem ("_Debug", "", () => {
                        state.EventsSink.EnqueueEvent(new AddPage<DebuggerState>($"Page {state.MachineStates.Count}", isExternalTrace: false));
                    }),
                    new MenuItem ("_Diff", "",  () => {
                        // open trace file
                        TraceStateEntry[] files = new TraceStateEntry[2];
                        for(int i = 0; i < 2; i++)
                        {
                            using var importOpenDialogue = new OpenDialog("traces File", $"Select the {i + 1}/2 json trace files", new List<string> { ".json" });

                            Application.Run(importOpenDialogue);

                            if(importOpenDialogue.Canceled) return;
                            string filePath = (string)importOpenDialogue.FilePath;
                            string fileName = Path.GetFileNameWithoutExtension (filePath);
                            try {
                                files[i] = new TraceStateEntry(fileName, new MachineState(JsonConvert.DeserializeObject<GethLikeTxTrace>(File.ReadAllText (filePath))));
                            } catch
                            {
                                MainView.ShowError("Failed to deserialize Traces Provided!");
                                return;
                            }
                        }
                        state.EventsSink.EnqueueEvent(new AddPage<TraceState>(String.Join("/", files.Select(f => f.Name)), new TraceState(files[0], files[1])));
                        return;
                    }),
                }),
            });
            IsCached = true;
        }

        return (menu, null);
    }
}
