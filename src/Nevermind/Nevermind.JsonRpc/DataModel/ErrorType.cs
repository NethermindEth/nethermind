namespace Nevermind.JsonRpc.DataModel
{
    public enum ErrorType
    {
        ParseError,
        InvalidRequest,
        MethodNotFound,
        InvalidParams,
        InternalError,
        ServerError
    }
}