# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "Verify-Axioms.ps1") -Lake $Lake -IncludePublication
