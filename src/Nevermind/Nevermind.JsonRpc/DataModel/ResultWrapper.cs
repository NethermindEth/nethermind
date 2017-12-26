using Nevermind.Utils.Model;

namespace Nevermind.JsonRpc.DataModel
{
    public class ResultWrapper<T> : IResultWrapper
    {
        public T Data { get; set; }
        public Result Result { get; set; }
        public ErrorType ErrorType { get; set; }

        public static ResultWrapper<T> Fail(string error)
        {
            return new ResultWrapper<T> { Result = Result.Fail(error), ErrorType = ErrorType.InternalError};
        }

        public static ResultWrapper<T> Fail(string error, ErrorType errorType)
        {
            return new ResultWrapper<T> { Result = Result.Fail(error), ErrorType = errorType};
        }

        public static ResultWrapper<T> Success(T data)
        {
            return new ResultWrapper<T> { Data = data, Result = Result.Success()};
        }

        public Result GetResult()
        {
            return Result;
        }

        public object GetData()
        {
            return Data;
        }
    }
}