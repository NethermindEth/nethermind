#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include CorpusParity.Helpers.cs
#:include PrepareEthCallCorpus.Helpers.cs

return PrepareEthCallCorpus.Run(args, Environment.GetEnvironmentVariable);
