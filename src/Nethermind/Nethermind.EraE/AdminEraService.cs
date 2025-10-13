// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.EraE;
public class AdminEraService(
    Era1.IEraImporter eraImporter,
    Era1.IEraExporter eraExporter,
    IProcessExitSource processExit,
    ILogManager logManager)
    : Era1.AdminEraService(eraImporter, eraExporter, processExit, logManager);