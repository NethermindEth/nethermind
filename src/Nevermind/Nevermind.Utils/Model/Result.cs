using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nevermind.Utils.Model
{
    public class Result
    {
        public ResultType ResultType { get; set; }
        public string Error { get; set; }

        public static Result Fail(string error)
        {
            return new Result {ResultType = ResultType.Failure, Error = error};
        }

        public static Result Success()
        {
            return new Result { ResultType = ResultType.Success };
        }
    }
}
