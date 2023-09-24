// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components;
using Nethermind.Evm.Lab.Components.TracerView;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Nethermind.Evm.Tracing.GethStyle;
using Terminal.Gui;
using Terminal.Gui.Trees;
using static Microsoft.FSharp.Core.ByRefKinds;

namespace Nethermind.Evm.Lab.Components.Differ;
internal class EntriesView : IComponent<MachineState>
{
    bool isCached = false;
    private TableViewColored? programView = null;
    private static string[] properties = new string[] { "Step", "Pc", "Operation", "Opcode", "GasCost", "Gas", "Depth", "Error" };
    private int startDiffColoringAt;
    public EntriesView(int DiffingIndex) => startDiffColoringAt = DiffingIndex;
    public (View, Rectangle?) View(MachineState state, Rectangle? rect = null)
    {

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );

        if (!isCached)
        {

            var dataTable = new DataTable();

            foreach (var prop in properties)
            {
                dataTable.Columns.Add(prop);
            }

            int RowIndex = 0;
            foreach (var entry in state.Entries)
            {
                var opcode = Enum.Parse<Evm.Instruction>(entry.Opcode);
                dataTable.Rows.Add(RowIndex++, $"0x{entry.ProgramCounter:X4}", opcode.ToString(), (int)opcode, entry.GasCost, entry.Gas, entry.Depth, entry.Error);
            }

            programView ??= new TableViewColored()
            {
                X = frameBoundaries.X,
                Y = frameBoundaries.Y,
                Width = frameBoundaries.Width,
                Height = frameBoundaries.Height,
            };

            programView.Table = dataTable;
            programView.LineLenght = 8;
            programView.OverrideLineIndex += (table, word) =>
            {
                int lineIndex = 0;
                if (table.RenderCellIndex % table.LineLenght == 0
                    && Int32.TryParse(word, out lineIndex))
                {
                    return lineIndex;
                }
                else return null;
            };
            isCached = true;
        }
        programView.RenderCellIndex = 0;
        programView.ColoredRanges.Add(new Range(startDiffColoringAt, int.MaxValue));
        programView.SelectedRow = state.Index;

        return (programView, frameBoundaries);
    }

    public void Dispose()
    {
        programView?.Dispose();
    }
}
