// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class TransactionVerifierResult
    {
        public bool BlockFound { get; }
        public long Confirmations { get; }
        public long RequiredConfirmations { get; }
        public bool Confirmed { get; private set; }

        public TransactionVerifierResult(bool blockFound, long confirmations, long requiredConfirmations)
        {
            BlockFound = blockFound;
            Confirmations = confirmations;
            RequiredConfirmations = requiredConfirmations;
            Confirmed = blockFound && confirmations >= requiredConfirmations;
        }
    }
}
