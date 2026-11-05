// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Stateless;

public sealed class WitnessValidationException(string message) : InvalidOperationException(message);
