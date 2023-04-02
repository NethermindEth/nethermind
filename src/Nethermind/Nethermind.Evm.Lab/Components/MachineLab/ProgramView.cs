// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants;
internal class ProgramView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private TableView? programView = null;
    private IReadOnlyList<(int Idx, string Operation)> Dissassemble(byte[] bytecode)
    {
        var opcodes = new List<(int Idx, string Operation)>();

        for (int i = 0; i < bytecode.Length; i++)
        {
            var instruction = (Instruction)bytecode[i];
            if (!instruction.IsValid())
            {
                opcodes.Add((i, "INVALID"));
                continue;
            }
            int immediatesCount = instruction.GetImmediateCount();
            byte[] immediates = bytecode.Slice(i + 1, immediatesCount);
            opcodes.Add((i, $"{instruction.ToString()} {immediates.ToHexString(immediates.Any())}"));
            i += immediatesCount;
        }
        return opcodes;
    }
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var dissassembledBytecode = Dissassemble(state.GetState().Bytecode);

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        container ??= new FrameView("ProgramState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        var dataTable = new DataTable();
        dataTable.Columns.Add("Position");
        dataTable.Columns.Add("Operation");
        foreach (var (k, v) in dissassembledBytecode)
        {
            dataTable.Rows.Add(k, v);
        }

        programView ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };
        programView.Table = dataTable;

        if (!isCached)
        {
            container.Add(programView);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
