// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Int256;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class StackView : IComponent<IEnumerable<byte[]>>
{
    bool isCached = false;
    private FrameView? container = null;
    private TableView? stackView = null;

    public void Dispose()
    {
        container?.Dispose();
        stackView?.Dispose();
    }

    public (View, Rectangle?) View(IEnumerable<byte[]> state, Rectangle? rect = null)
    {
        state = state.Reverse();
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        container ??= new FrameView("StackState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        stackView ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };

        var dataTable = new DataTable();
        dataTable.Columns.Add("Index");
        dataTable.Columns.Add("Value");

        int stringLen = 42;
        var cleanedUpDataSource = state.Select(entry =>
        {

            string entryStr = entry.ToHexString(false);
            if (entryStr.Length < stringLen)
            {
                entryStr = entryStr.PadLeft(stringLen - entryStr.Length, '0');
            }
            else
            {
                entryStr = entryStr.Substring(entryStr.Length - stringLen);
            }
            return entryStr;
        }).ToList();

        int index = 0;
        foreach (var value in cleanedUpDataSource)
        {
            dataTable.Rows.Add(index++, value);
        }
        stackView.Table = dataTable;

        if (!isCached)
            container.Add(stackView);
        isCached = true;
        return (container, frameBoundaries);
    }
}
