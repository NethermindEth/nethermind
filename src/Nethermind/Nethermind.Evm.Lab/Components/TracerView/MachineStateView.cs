// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using MachineStateEvents;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class MachineDataView : IComponent<GethTxTraceEntry>
{
    bool isCached = false;
    private FrameView? container = null;
    private (TableView? generalState, TableView? opcodeData) machinView = (null, null);

    private static readonly string[] Columns_Overview = { "Pc", "Gas", "Depth", "Error" };
    private static readonly string[] Columns_Opcode = { "Opcode", "Operation", "GasCost" };
    public (View, Rectangle?) View(GethTxTraceEntry state, Rectangle? rect = null)
    {
        var frameBoundaries = new Rectangle(
            X: rect?.X ?? 0,
            Y: rect?.Y ?? 0,
            Width: rect?.Width ?? 50,
            Height: rect?.Height ?? 10
        );
        container ??= new FrameView("ProcessorState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        var dataTable = new DataTable();
        foreach (var h in Columns_Overview)
        {
            dataTable.Columns.Add(h);
        }

        if (state is not null)
            dataTable.Rows.Add(
                Columns_Overview.Select(propertyName => typeof(GethTxTraceEntry).GetProperty(propertyName)?.GetValue(state)).ToArray()
            );

        var opcodeData = new DataTable();
        foreach (var h in Columns_Opcode)
        {
            opcodeData.Columns.Add(h);
        }

        if (state is not null)
            opcodeData.Rows.Add(
                Columns_Opcode.Select(
                    proeprtyName =>
                    {
                        if (proeprtyName == "Opcode")
                        {
                            var opcodeName = state.Opcode;
                            var Instruction = (byte)Enum.Parse<Evm.Instruction>(opcodeName);
                            return (Object?)$"{Instruction:X4}";
                        }
                        else
                        {
                            return typeof(GethTxTraceEntry).GetProperty(proeprtyName)?.GetValue(state);
                        }
                    }
                ).ToArray()
            );

        machinView.generalState ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Percent(50),
        };
        machinView.generalState.Table = dataTable;

        machinView.opcodeData ??= new TableView()
        {
            X = 0,
            Y = Pos.Bottom(machinView.generalState),
            Width = Dim.Fill(2),
            Height = Dim.Percent(50),
        };
        machinView.opcodeData.Table = opcodeData;

        if (!isCached)
        {
            container.Add(machinView.generalState, machinView.opcodeData);
        }
        isCached = true;
        return (container, frameBoundaries);
    }

    public void Dispose()
    {
        container?.Dispose();
    }
}
