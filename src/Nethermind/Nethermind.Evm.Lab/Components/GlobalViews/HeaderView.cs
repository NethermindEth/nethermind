// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineState.Actions;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.GlobalViews;
internal class HeaderView : IComponent<MachineState>
{
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var menu = new MenuBar(new MenuBarItem[] {
            new MenuBarItem ("_File", new MenuItem [] {
                new MenuItem ("_Run", "", () => {
                    var fileOpenDialogue = new OpenDialog("Bytecode File", "Select a binary file that contains EVM bytecode");
                    state.GetState().EnqueueEvent(new FileLoaded((string)fileOpenDialogue.FilePath));
                }),
                new MenuItem ("_Open", "", () => {
                    // open trace file
                    var fileOpenDialogue = new OpenDialog("Traces File", "Select a file that contains EVM run traces");
                    state.GetState().EnqueueEvent(new TracesLoaded((string)fileOpenDialogue.FilePath));
                }),
                new MenuItem ("_Quit", "", () => {
                    Application.RequestStop ();
                })
            }),
        });

        return (menu, null);
    }
}
