// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants;
internal class ProgramView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private TableView? programView = null;
    
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var dissassembledBytecode = BytecodeParser.Dissassemble(state.GetState().Bytecode);

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
        foreach (var instr in dissassembledBytecode)
        {
            dataTable.Rows.Add(instr.idx, instr.ToString());
        }

        programView ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(80),
        };
        programView.Table = dataTable;
        programView.SelectedRow = state.GetState().Index;

        if (!isCached)
        {
            var mediaLikeView = new MediaLikeView()
                .View(state, new Rectangle
                {
                    X = 0,
                    Y = Pos.Bottom(programView),
                    Height = Dim.Percent(20),
                });
            container.Add(programView, mediaLikeView.Item1);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
