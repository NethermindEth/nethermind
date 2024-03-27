// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.Plugin.Data;

public enum InclusionListStatus {
    VALID,
    INVALID,
    SYNCING,
    ACCEPTED
}

public class InclusionListStatusV1
{
    public InclusionListStatusV1(InclusionListStatus status, String? validationError)
    {
        Status = status;
        ValidationError = validationError;
    }

    public String? ValidationError { get; set; }
    public InclusionListStatus Status { get; set; }
}
