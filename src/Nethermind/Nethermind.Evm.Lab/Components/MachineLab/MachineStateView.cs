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
        private static readonly string[] Columns = { "Pc", "Gas", "Depth", "Error" };
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

        public (View, Rectangle) View(IState<MachineState> state, Rectangle? rect = null)
        {
            var innerState = state.GetState();

            var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
            var frameView = new FrameView("ProcessorState")
            {
                X = frameBoundaries.X,
                Y = frameBoundaries.Y,
                Width = frameBoundaries.Width,
                Height = frameBoundaries.Height,
            };

            var dataTable = new DataTable();
            foreach (var h in Columns)
            {
                dataTable.Columns.Add(h);
            }

            dataTable.Rows.Add(
                Columns.Select(propertyName => typeof(GethTxTraceEntry).GetProperty(propertyName)?.GetValue(innerState.Current)).ToArray()
            );

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
}
