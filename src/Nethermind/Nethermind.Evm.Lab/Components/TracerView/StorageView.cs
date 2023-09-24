// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class StorageView : IComponent<Dictionary<string, string>>
{
    bool isCached = false;
    private FrameView? tableContainer = null;
    private TableView? tableview = null;
    private FrameView? valueContainer = null;
    private TextView valuebox = null;
    private Dictionary<string, string> cachedState;
    public void Dispose()
    {
        tableContainer?.Dispose();
        valueContainer?.Dispose();
        tableview?.Dispose();
    }

    public (View, Rectangle?) View(Dictionary<string, string> state, Rectangle? rect = null)
    {
        cachedState = state;    
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        tableContainer ??= new FrameView("StorageState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        var dataTable = new DataTable();
        dataTable.Columns.Add(string.Empty);
        dataTable.Columns.Add("Address");
        if (state is not null)
        {
            foreach (var (k, _) in state)
            {
                dataTable.Rows.Add("Address", k);
            }
        }

        tableview ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(65),
        };

        string value = "[Please select a storage key]";


        valueContainer ??= new FrameView("StorageValue")
        {
            Y = Pos.Bottom(tableview),
            X = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        valuebox ??= new TextView()
        {
            X = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = value,
            Enabled = false
        };

        tableview.Table = dataTable;
        if (!isCached)
        {
            valueContainer.Add(valuebox);
            tableview.SelectedCellChanged += (e) =>
            {
                if(e.NewRow >= 0 && e.NewRow < cachedState.Count)
                {
                    string value = cachedState[(string)tableview.Table.Rows[e.NewRow]["Address"]];
                    valuebox.Text = value;
                }
            };
            tableContainer.Add(tableview, valueContainer);
        }
        isCached = true;
        return (tableContainer, frameBoundaries);
    }
}
