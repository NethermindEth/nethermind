// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NStack;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;

internal class FilteredInputField : TextField
{
    protected List<Func<ustring, bool>> _filters = new();
    public FilteredInputField(ustring defaultValue) : base(defaultValue.ToString()) { }
    public FilteredInputField(Func<ustring, bool> filter, ustring defaultValue) : base(defaultValue)
        => _filters.Add(filter);

    public override TextChangingEventArgs OnTextChanging(ustring newText)
    {
        if (_filters.Any(filter => filter(newText)))
            return new TextChangingEventArgs(this.Text);
        return base.OnTextChanging(newText);
    }
}
internal class NumberInputField : FilteredInputField
{
    public NumberInputField(long defaultValue) : base(defaultValue.ToString())
        => _filters.Add(bool (ustring s) => s.Where(c => Char.IsAsciiLetter((char)c)).Any());

    public void AddFilter(Func<ustring, bool> filter)
        => _filters.Add(filter);
}
