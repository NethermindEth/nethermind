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
            string result = "{% " + tab + " %}";
            docBuilder.AppendLine(result);
        }

        public void CloseTab(StringBuilder docBuilder)
        {
            docBuilder.AppendLine("{% endtab %}");
        }

        public void CreateCodeBlock(StringBuilder docBuilder, string code)
        {
            string codeBlock = $"```text \n {code} \n ```";
            docBuilder.AppendLine(code);
        }
    }
}