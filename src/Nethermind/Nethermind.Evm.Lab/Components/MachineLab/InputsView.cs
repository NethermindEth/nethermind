// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineState.Actions;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.MachineLab;
internal class InputsView : IComponent<MachineState>
{
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var innerState = state.GetState();

        var streamFromBuffer = new MemoryStream(innerState.ReturnValue);

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        var frameView = new FrameView("InputsPanel")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        var label_bytecode = new Label("Bytecode Inout Area")
        {
            Width = Dim.Fill(),
            Height = Dim.Percent(10)
        };
        var bytecodeBox = new TextField("[<Insert Bytecode here in HEX>]")
        {
            Y = Pos.Bottom(label_bytecode),
            Width = Dim.Fill(),
            Height = Dim.Percent(40)
        };

        var label_calldata = new Label("Calldata Inout Area")
        {
            Y = Pos.Bottom(bytecodeBox),
            Width = Dim.Fill(),
            Height = Dim.Percent(10)
        };
        var CallDataBox = new TextField("[<Insert Bytecode here in HEX>]")
        {
            Y = Pos.Bottom(label_calldata),
            Width = Dim.Fill(),
            Height = Dim.Percent(20)
        };

        bytecodeBox.TextChanged += (e) =>
        {
            innerState.Events.Enqueue(new BytecodeInserted((String)e));
        };
        CallDataBox.TextChanged += (e) =>
        {
            innerState.Events.Enqueue(new CallDataInserted((String)e));
        };

        frameView.Add(label_bytecode, bytecodeBox, label_calldata, CallDataBox);
        return (frameView, frameBoundaries);
    }
}
