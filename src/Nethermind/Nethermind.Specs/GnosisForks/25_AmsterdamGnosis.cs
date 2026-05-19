// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class AmsterdamGnosis() : NamedGnosisReleaseSpec<AmsterdamGnosis>(Amsterdam.Instance, OsakaGnosis.Instance);
