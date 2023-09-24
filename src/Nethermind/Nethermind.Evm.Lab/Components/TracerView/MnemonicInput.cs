// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using MachineStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class MnemonicInput : IComponent<(CodeInfo Bytecode, IReleaseSpec Spec)>
{
    private class CodeSection
    {
        public CodeSection(int iCount, int oCount, int sHeight)
            => (inCount, outCount, stackMax) = (iCount, oCount, sHeight);
        public int inCount = 0;
        public int outCount = 0;
        public int stackMax = 0;
        public string Body = string.Empty;
    }
    // keep view static and swap state instead 
    private bool isCached = false;
    private Dialog? container = null;
    private View? codeView = null;
    private CodeSection sectionsField = null;
    private (Button submit, Button cancel) buttons;
    private bool isEofMode = false;
    public event Action<byte[]> BytecodeChanged;


    public void Dispose()
    {
        container?.Dispose();
        buttons.cancel?.Dispose();
        buttons.submit?.Dispose();
    }
    private void SubmitBytecodeChanges(CodeSection functionsBytecodes)
    {
        byte[] bytecode = Array.Empty<byte>();
        bytecode = BytecodeParser.Parse(sectionsField.Body.Trim()).ToByteArray();
        BytecodeChanged?.Invoke(bytecode);
    }

    private bool CreateNewFunctionPage(bool isFirstRender, out TextView textView)
    {
        if ((!isFirstRender && !isEofMode) || sectionsField is null)
        {
            textView = null;
            return false;
        }

        sectionsField = new CodeSection(0, 0, 0);
        var container = new View()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = Colors.Menu,
        };

        var inputBodyField = new Terminal.Gui.TextView
        {
            Width = Dim.Fill(),
            Height = Dim.Percent(100),
            ColorScheme = Colors.Base
        };
        inputBodyField.Initialized += (s, e) =>
        {
            sectionsField.Body = (string)inputBodyField.Text;
        };

        inputBodyField.KeyPress += (_) =>
        {
            sectionsField.Body = (string)inputBodyField.Text;
        };

        container.Add(
            inputBodyField
        );

        codeView.Add(container);
        textView = inputBodyField;
        return true;
    }

    public (View, Rectangle?) View((CodeInfo Bytecode, IReleaseSpec Spec) state, Rectangle? rect = null)
    {
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? Pos.Center(),
                Y: rect?.Y ?? Pos.Center(),
                Width: rect?.Width ?? Dim.Percent(25),
                Height: rect?.Height ?? Dim.Percent(75)
            );

        codeView ??= new View()
        {
            Width = Dim.Fill(),
            Height = Dim.Percent(95),
        };

        sectionsField = new CodeSection(0, 0, 0);
        CreateNewFunctionPage(isFirstRender: true, out var bodyInputFieldRef);
        var bytecodeMnemonics = BytecodeParser.Dissassemble(state.Bytecode.MachineCode)
            .ToMultiLineString(state.Spec);
        bodyInputFieldRef.Text = bytecodeMnemonics;

        buttons.submit ??= new Button("Submit");
        buttons.cancel ??= new Button("Cancel");
        container ??= new Dialog("Bytecode Insertion View", 100, 7, buttons.submit, buttons.cancel)
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
            ColorScheme = Colors.TopLevel
        };

        if (!isCached)
        {
            container.Add(codeView);
            buttons.submit.Clicked += () =>
            {
                try
                {
                    SubmitBytecodeChanges(sectionsField);
                    Application.RequestStop();
                }
                catch (Exception ex)
                {
                    MainView.ShowError(ex.Message);
                }
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
