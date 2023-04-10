// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Text.RegularExpressions;
using MachineState.Actions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Nethermind.Specs;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.MachineLab;
internal class MnemonicInput : IComponent<MachineState>
{
    bool isCached = false;
    private Dialog? container = null;
    private TextView? inputField= null;
    private (Button submit, Button cancel) buttons;
    public event Action<byte[]> BytecodeChanged; 
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var innerState = state.GetState();


        var bytecodeMnemonics = BytecodeParser.Dissassemble(innerState.Bytecode).ToMultiLineString();
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? Pos.Center(),
                Y: rect?.Y ?? Pos.Center(),
                Width: rect?.Width ?? Dim.Percent(20),
                Height: rect?.Height ?? Dim.Percent(75)
            );

        inputField ??= new Terminal.Gui.TextView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Border = new Border()
        };
        inputField.Text = bytecodeMnemonics;


        buttons.submit ??= new Button("Submit");
        buttons.cancel ??= new Button("Cancel");
        container ??= new Dialog("Eip Selection Panel", 60, 7, buttons.submit, buttons.cancel)
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
            ColorScheme = Colors.TopLevel,
        };

        if (!isCached)
        {
            container.Add(inputField);
            buttons.submit.Clicked += () =>
            {
                try
                {
                    var newBytecode = BytecodeParser.Parse((string)inputField.Text.TrimSpace()).ToByteArray();
                    BytecodeChanged?.Invoke(newBytecode);
                    EventsSink.EnqueueEvent(new BytecodeInsertedB(newBytecode));
                } catch (Exception ex)
                {
                    EventsSink.EnqueueEvent(new ThrowError("Error parsing Mnemonics"));
                }
                Application.RequestStop();
            };

            buttons.cancel.Clicked += () =>
            {
                Application.RequestStop();
            };

        }
        isCached = true;

        return (container, frameBoundaries);
    }
}
