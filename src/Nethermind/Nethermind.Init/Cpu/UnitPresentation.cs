// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

namespace Nethermind.Init.Cpu;

internal class UnitPresentation
{
    public static readonly UnitPresentation Default = new UnitPresentation(isVisible: true, 0);

    public static readonly UnitPresentation Invisible = new UnitPresentation(isVisible: false, 0);

    public bool IsVisible { get; private set; }

    public int MinUnitWidth { get; private set; }

    public UnitPresentation(bool isVisible, int minUnitWidth)
    {
        IsVisible = isVisible;
        MinUnitWidth = minUnitWidth;
    }

    public static UnitPresentation FromVisibility(bool isVisible)
    {
        return new UnitPresentation(isVisible, 0);
    }

    public static UnitPresentation FromWidth(int unitWidth)
    {
        return new UnitPresentation(isVisible: true, unitWidth);
    }
}
