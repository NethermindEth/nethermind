// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MachineStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class InputsView : IComponent<(CodeInfo CodeInfo, byte[] CallData, IReleaseSpec Spec)>
{
    bool isCached = false;
    private FrameView? container = null;
    private TextField? bytecodeInputField = null;
    private TextField? calldataInputField = null;
    private Button? mnemonicInputViewBtn = null;

    public void Dispose()
    {
        container?.Dispose();
        bytecodeInputField?.Dispose();
        calldataInputField?.Dispose();
        mnemonicInputViewBtn?.Dispose();
    }

    public event Action<ActionsBase> InputChanged;

    public (View, Rectangle?) View((CodeInfo CodeInfo, byte[] CallData, IReleaseSpec Spec) state, Rectangle? rect = null)
    {
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
        bytecodeInputField ??= new TextField(state.CodeInfo.MachineCode.ToHexString(true))
        {
            Y = Pos.Bottom(label_bytecode),
            Width = Dim.Percent(80),
            Height = Dim.Percent(40),
            ColorScheme = Colors.Base
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
        calldataInputField ??= new TextField(state.CallData.ToHexString(true))
        {
            Y = Pos.Bottom(label_calldata),
            Width = Dim.Fill(),
            Height = Dim.Percent(20),
            Enabled = false,
            ColorScheme = Colors.Base
        };

        if (!isCached)
        {

            bytecodeInputField.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Enter && bytecodeInputField.HasFocus)
                {
                    InputChanged?.Invoke(new BytecodeInserted((string)bytecodeInputField.Text));
                }
            };
            calldataInputField.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Enter && calldataInputField.HasFocus)
                {
                    InputChanged?.Invoke(new CallDataInserted((string)calldataInputField.Text));
                }
            };

            mnemonicInputViewBtn.Clicked += () =>
            {
                var mnemonicInputView = new MnemonicInput();
                mnemonicInputView.BytecodeChanged += (nbcode) =>
                {
                    bytecodeInputField.Text = nbcode.ToHexString(true);
                    InputChanged?.Invoke(new BytecodeInserted((string)bytecodeInputField.Text));
                };
                Application.Run((Dialog)mnemonicInputView.View((state.CodeInfo, state.Spec)).Item1);
            };

            container.Add(label_bytecode, bytecodeInputField, label_calldata, calldataInputField, mnemonicInputViewBtn);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
