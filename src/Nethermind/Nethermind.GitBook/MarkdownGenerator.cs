// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;

namespace Nethermind.GitBook
{
    public class MarkdownGenerator
    {
        public void OpenTabs(StringBuilder docBuilder)
        {
            docBuilder.AppendLine("{% tabs %}");
        }

        public void CloseTabs(StringBuilder docBuilder)
        {
            docBuilder.AppendLine("{% endtabs %}");
        }

        public void CreateTab(StringBuilder docBuilder, string tabName)
        {
            string tab = $"tab title=\"{tabName}\"";
            string result = string.Concat("{% ", tab, " %}");
            docBuilder.AppendLine(result);
        }

        public void CloseTab(StringBuilder docBuilder)
        {
            docBuilder.AppendLine("{% endtab %}");
        }

        public void CreateCodeBlock(StringBuilder docBuilder, string code)
        {
            string codeBlock = $"```yaml\n{code}\n```";
            docBuilder.AppendLine(codeBlock);
        }

        public void CreateEdgeCaseHint(StringBuilder docBuilder, string hint)
        {
            docBuilder.AppendLine("{% hint style=\"info\" %}");
            docBuilder.AppendLine($"**Hint:** {hint}");
            docBuilder.AppendLine("{% endhint %}");
        }

        public void CreateRpcInvocationExample(StringBuilder docBuilder, string method, List<string> arguments)
        {
            docBuilder.Append("`");
            docBuilder.Append(GetRpcInvocationExample(method, arguments));
            docBuilder.AppendLine("`");
        }
        public void CreateCurlExample(StringBuilder docBuilder, string method, List<string> arguments)
        {
            string rpcInvocationExample = GetRpcInvocationExample(method, arguments);
            string exampleWithoutClosingBracket = rpcInvocationExample.Substring(0, rpcInvocationExample.Length - 1);
            docBuilder.Append(string.Concat("```\ncurl --data '", exampleWithoutClosingBracket, ",\"id\":1,\"jsonrpc\":\"2.0\"}' -H \"Content-Type: application/json\" -X POST localhost:8545\n```\n"));
        }

        public string GetRpcInvocationExample(string method, List<string> arguments)
        {
            StringBuilder parameters = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (parameters.Length > 0) parameters.Append(", ");
                parameters.Append(argument);
            }
            return string.Concat("{\"method\":\"", method, "\",\"params\":[", parameters, "]}");
        }

        public string GetCliInvocationExample(string methodName, List<string> arguments, bool isFunction)
        {
            StringBuilder parameters = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (parameters.Length > 0)
                {
                    parameters.Append(", ");
                }
                else
                {
                    parameters.Append('(');
                }
                parameters.Append(argument);
            }

            if (parameters.Length > 0) parameters.Append(')');

            if (parameters.Length == 0
                && isFunction) parameters.Append("()");

            return string.Concat(methodName, parameters);
        }
    }
}
