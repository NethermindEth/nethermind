// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.Differ;

class TableViewColored : TableView
{
    public List<Range> ColoredRanges = new List<Range>();
    public int HighlightedRow = -1;
    public int LineLenght = 8;
    public int DiffIndexStart = 2;
    public int RenderCellIndex = 0;
    public int RenderLineIndex = 0;
    public bool RenderPastDiffLine(int line)
    {
        return ColoredRanges.Any(range => line < range.End.Value && line >= range.Start.Value);
    }

    public Func<TableViewColored, string, int?> OverrideLineIndex;
    public override void Redraw(Rect bounds)
    {
        RenderCellIndex = 0;
        base.Redraw(bounds);
    }

    public void ClearColoredRegions() => ColoredRanges.Clear();

    public void HighlightRow(int rowIndex)
    {
        HighlightedRow = rowIndex;
    }

    protected override void RenderCell(Terminal.Gui.Attribute cellColor, string render, bool isPrimaryCell)
    {

        RenderLineIndex = OverrideLineIndex?.Invoke(this, render) ?? RenderLineIndex;
        for (int i = 0; i < render.Length; i++)
        {
            if (RenderPastDiffLine(RenderLineIndex))
            {
                if (RenderCellIndex % 8 == 0)
                {
                    Driver.SetAttribute(Driver.MakeAttribute(RenderLineIndex == SelectedRow ? Color.Brown : Color.Magenta, cellColor.Background));
                }
                else
                {
                    Driver.SetAttribute(Driver.MakeAttribute(Color.Red, cellColor.Background));
                }
            }
            else
            {
                if (RenderLineIndex == HighlightedRow)
                {
                    if (RenderCellIndex % 8 == 0)
                    {
                        Driver.SetAttribute(Driver.MakeAttribute(Color.Brown, cellColor.Background));
                    }
                    else
                    {
                        Driver.SetAttribute(Driver.MakeAttribute(Color.BrightMagenta, cellColor.Background));
                    }
                }
                else
                {
                    Driver.SetAttribute(cellColor);
                }
            }

            Driver.AddRune(render[i]);
        }
        RenderCellIndex++;
    }
}
