// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using MachineState.Actions;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants
{
    internal class MachineOverview : IComponent<MachineState>
    {
        bool isCached = false;
        private FrameView? container = null;
        private (TableView? generalState, TableView? opcodeData) machinView = (null, null);

        private static readonly string[] Columns_Overview = { "Pc", "Gas", "Depth", "Error" };
        private static readonly string[] Columns_Opcode = { "Opcode", "Operation", "GasCost" };
        private View? _cache { get; set; }
        public IState<MachineState> Update(IState<MachineState> currentState, ActionsBase action)
        {
            var innerState = currentState.GetState();
            return action switch
            {
                MoveNext _ => innerState?.Next(),
                MoveBack _ => innerState?.Previous(),
                Goto act => innerState?.Goto(act.index),
                _ => currentState
            };
        }

        public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
        {
            var innerState = state.GetState();

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

            dataTable.Rows.Add(
                Columns_Overview.Select(propertyName => typeof(GethTxTraceEntry).GetProperty(propertyName)?.GetValue(innerState.Current)).ToArray()
            );

            var opcodeData = new DataTable();
            foreach (var h in Columns_Opcode)
            {
                opcodeData.Columns.Add(h);
            }
            opcodeData.Rows.Add(
                Columns_Opcode.Select(
                    proeprtyName =>
                    {
                        if (proeprtyName == "Opcode")
                        {
                            var opcodeNane = innerState.Current.Operation;
                            var Instruction = (byte)Enum.Parse<Evm.Instruction>(opcodeNane);
                            return (Object?)$"{Instruction:X4}";
                        }
                        else
                        {
                            return typeof(GethTxTraceEntry).GetProperty(proeprtyName)?.GetValue(innerState.Current);
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
    }
}
