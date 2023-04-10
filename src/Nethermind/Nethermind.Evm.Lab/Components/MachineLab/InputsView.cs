// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineState.Actions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.MachineLab;
internal class InputsView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private TextField? bytecodeInputField = null;
    private TextField? calldataInputField = null;
    private Button? mnemonicInputViewBtn = null;
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
        container ??= new FrameView("InputsPanel")
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
        bytecodeInputField ??= new TextField(state.GetState().Bytecode.ToHexString(true))
        {
            Y = Pos.Bottom(label_bytecode),
            Width = Dim.Percent(80),
            Height = Dim.Percent(40)
        };

        mnemonicInputViewBtn ??= new Button("MnemonicInput")
        {
            X = Pos.Right(bytecodeInputField),
            Y = Pos.Bottom(label_bytecode),
            Width = Dim.Percent(20),
        };

        var label_calldata = new Label("Calldata Inout Area")
        {
            Y = Pos.Bottom(bytecodeInputField),
            Width = Dim.Fill(),
            Height = Dim.Percent(10)
        };
        calldataInputField ??= new TextField(state.GetState().CallData.ToHexString(true))
        {
            Y = Pos.Bottom(label_calldata),
            Width = Dim.Fill(),
            Height = Dim.Percent(20)
        };

        if (!isCached)
        {
            bytecodeInputField.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Enter)
                {
                    EventsSink.EnqueueEvent(new BytecodeInserted((string)bytecodeInputField.Text));
                }
            };
            calldataInputField.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Enter)
                {
                    EventsSink.EnqueueEvent(new CallDataInserted((string)calldataInputField.Text));
                }
            };

            mnemonicInputViewBtn.Clicked += () =>
            {
                var mnemonicInputView = new MnemonicInput();
                mnemonicInputView.BytecodeChanged += (nbcode) =>
                {
                    bytecodeInputField.Text = nbcode.ToHexString(true);
                };
                Application.Run((Dialog)mnemonicInputView.View(state).Item1);
            };

            container.Add(label_bytecode, bytecodeInputField, label_calldata, calldataInputField, mnemonicInputViewBtn);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
