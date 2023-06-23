// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public class Result
    {
        private Result() { }

        public ResultType ResultType { get; set; }

        public string? Error { get; set; }

        public static Result Fail(string error) => new() { ResultType = ResultType.Failure, Error = error };
        
        public static Result Success { get; } = new() { ResultType = ResultType.Success };
    }
}
