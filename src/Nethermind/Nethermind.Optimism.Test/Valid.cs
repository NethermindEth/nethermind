// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;

namespace Nethermind.Optimism.Test;

/// <summary>
/// Explicitly describes at which timestamp ranges a test case should be valid.
/// </summary>
public class Valid
{
    private readonly bool _valid;
    private readonly ulong? _from;
    private readonly ulong? _to;

    private readonly Valid[]? _validations;

    private Valid(bool valid, ulong? from = null, ulong? to = null)
    {
        _valid = valid;
        _from = from;
        _to = to;
    }

    private Valid(params Valid[] validations)
    {
        _validations = validations;
    }

    public static readonly Valid Always = new(true);
    public static readonly Valid Never = new(false);

    public static Valid Since(ulong from) => new(true, from);
    public static Valid Before(ulong to) => new(true, null, to);
    public static Valid Between(ulong from, ulong to) => new(true, from, to);

    public static Valid operator |(Valid v1, Valid v2)
    {
        if (v1 == Never) return v2;
        if (v2 == Never) return v1;
        if (v1 == Always || v2 == Always) return Always;

        return new(v1, v2);
    }

    public bool On(ulong timestamp)
    {
        if (_validations is not null)
            return _validations.Any(v => v.On(timestamp));

        return (_from is null || timestamp >= _from) && (_to is null || timestamp < _to) ? _valid : !_valid;
    }

    public override string ToString() => ToString(true);

    private string ToString(bool withPrefix)
    {
        if (_validations is not null)
            return string.Join(";", _validations.Select((v, i) => v.ToString(withPrefix && i == 0)));

        var prefix = withPrefix ? "Valid:" : "";
        var fromName = _from is { } f ? Fork.StartingAt(f)?.Name ?? $"{f}" : null;
        var toName = _to is { } t ? Fork.StartingAt(t)?.Name ?? $"{t}" : null;

        return (fromName, toName) switch
        {
            (null, null) => _valid ? $"{prefix} always" : $"{prefix} never",
            (string from, null) => $"{prefix} since {from}",
            (null, string to) => $"{prefix} before {to}",
            (string from, string to) => $"{prefix} between {from} and {to}"
        };
    }
}
