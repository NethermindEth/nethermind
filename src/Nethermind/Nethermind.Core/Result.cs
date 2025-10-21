// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public readonly record struct Result(ResultType ResultType, string? Error = null)
    {
        public static Result Fail(string error) => new() { ResultType = ResultType.Failure, Error = error };
        public static Result Success { get; } = new() { ResultType = ResultType.Success };
        public static implicit operator bool(Result result) => result.ResultType == ResultType.Success;
    }

    public readonly record struct Result<TData>(ResultType ResultType, TData? Data = default, string? Error = null)
    {
        public static Result<TData> Fail(string error, TData? data = default) => new() { ResultType = ResultType.Failure, Error = error, Data = data };
        public static Result<TData> Success(TData data) => new() { ResultType = ResultType.Success, Data = data };
        public static implicit operator bool(Result<TData> result) => result.ResultType == ResultType.Success;
        public static implicit operator Result<TData>(TData data) => Success(data);
        public static implicit operator Result<TData>(string error) => Fail(error);
        public static implicit operator (TData?, bool)(Result<TData> result) => (result.Data, result.ResultType == ResultType.Success);

        public void Deconstruct(out TData? result, out bool success)
        {
            result = Data;
            success = ResultType == ResultType.Success;
        }
    }
}
