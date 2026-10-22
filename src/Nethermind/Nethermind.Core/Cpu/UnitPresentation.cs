// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

namespace Nethermind.Core.Cpu;

public class UnitPresentation(bool isVisible, int minUnitWidth)
{
    public static readonly UnitPresentation Default = new(isVisible: true, 0);

    public static readonly UnitPresentation Invisible = new(isVisible: false, 0);

    public bool IsVisible { get; private set; } = isVisible;

    public int MinUnitWidth { get; private set; } = minUnitWidth;

    public static UnitPresentation FromVisibility(bool isVisible) => new(isVisible, 0);

    public static UnitPresentation FromWidth(int unitWidth) => new(isVisible: true, unitWidth);
}
