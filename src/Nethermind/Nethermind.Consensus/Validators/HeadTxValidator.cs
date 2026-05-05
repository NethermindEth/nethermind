// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Validators;

public sealed class HeadTxValidator() :
    CompositeTxValidator(
        MaxBlobCountBlobTxValidator.Instance,
        GasLimitCapTxValidator.Instance,
        MempoolBlobTxProofVersionValidator.Instance
    );
