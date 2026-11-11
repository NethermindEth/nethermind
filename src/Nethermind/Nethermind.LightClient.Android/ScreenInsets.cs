// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Graphics;
using Android.Views;

namespace Nethermind.LightClient.Android;

/// <summary>Keeps scroll content clear of system bars, display cutouts and the keyboard.</summary>
internal sealed class ScreenInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
{
    /// <inheritdoc/>
    public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
    {
        int left, top, right, bottom;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            using Insets occupied = insets.GetInsets(WindowInsets.Type.SystemBars() |
                WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
            left = occupied.Left;
            top = occupied.Top;
            right = occupied.Right;
            bottom = occupied.Bottom;
        }
        else
        {
            left = insets.SystemWindowInsetLeft;
            top = insets.SystemWindowInsetTop;
            right = insets.SystemWindowInsetRight;
            bottom = insets.SystemWindowInsetBottom;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                using DisplayCutout? cutout = insets.DisplayCutout;
                if (cutout is not null)
                {
                    left = Math.Max(left, cutout.SafeInsetLeft);
                    top = Math.Max(top, cutout.SafeInsetTop);
                    right = Math.Max(right, cutout.SafeInsetRight);
                    bottom = Math.Max(bottom, cutout.SafeInsetBottom);
                }
            }
        }

        view.SetPadding(left, top, right, bottom);
        return insets;
    }
}
