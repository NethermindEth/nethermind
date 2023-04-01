// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants;
internal class ProgramView : IComponent<MachineState>
{
    private IReadOnlyList<(int Idx, string Operation)> Dissassemble(byte[] bytecode) => new List<(int Idx, string Operation)>()
    {
        (0, "PUSH 0"),
        (2, "PUSH 2"),
        (4, "MSTORE8"),
        (5, "PUSH 0"),
        (7, "PUSH 2"),
        (9, "return"),
    };
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var dissassembledBytecode = Dissassemble(Core.Extensions.Bytes.FromHexString(state.GetState().Bytecode));

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        var frameView = new FrameView("ProgramState")
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

        frameView.Add(new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            Table = dataTable
        });
        return (frameView, frameBoundaries);
    }
}
