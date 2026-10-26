#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include Profiles.Helpers.cs

foreach (var (stack, count) in Profiles.Fold(Console.In))
    Console.WriteLine($"{stack} {count}");
