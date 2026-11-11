// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Content;
using Nethermind.LightClient.Mobile;

namespace Nethermind.LightClient.Android;

internal static class TrackingPreferences
{
    private const string Name = "tracking";

    internal static MobileTrackingSettings? Load(Context context)
    {
        using ISharedPreferences preferences = context.GetSharedPreferences(Name, FileCreationMode.Private)!;
        MobileTrackingSettings.TryCreate(preferences.GetString("account", null),
            preferences.GetBoolean("usdt", true), preferences.GetBoolean("dai", true), preferences.GetBoolean("usdc", true),
            out MobileTrackingSettings? settings);
        return settings;
    }

    internal static bool Save(Context context, MobileTrackingSettings settings)
    {
        using ISharedPreferences preferences = context.GetSharedPreferences(Name, FileCreationMode.Private)!;
        using ISharedPreferencesEditor editor = preferences.Edit()!;
        editor.PutString("account", settings.AccountAddress);
        editor.PutBoolean("usdt", settings.Usdt);
        editor.PutBoolean("dai", settings.Dai);
        editor.PutBoolean("usdc", settings.Usdc);
        return editor.Commit();
    }

    internal static bool Clear(Context context)
    {
        using ISharedPreferences preferences = context.GetSharedPreferences(Name, FileCreationMode.Private)!;
        using ISharedPreferencesEditor editor = preferences.Edit()!;
        editor.Clear();
        return editor.Commit();
    }
}
