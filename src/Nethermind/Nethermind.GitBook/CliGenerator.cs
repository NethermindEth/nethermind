using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Cli.Modules;

namespace Nethermind.GitBook
{
    public class CliGenerator
    {
        private readonly MarkdownGenerator _markdownGenerator;
        private readonly SharedContent _sharedContent;
        
        public CliGenerator(MarkdownGenerator markdownGenerator, SharedContent sharedContent)
        {
           _markdownGenerator = markdownGenerator;
           _sharedContent = sharedContent;
        }

        public void Generate()
        {
            string docsDir = DocsDirFinder.FindCli();
            List<Type> cliTypes = GetCliModules();

            foreach (Type cliType in cliTypes)
            {
                GenerateDocFileContent(cliType, docsDir);
            }
        }

        private List<Type> GetCliModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.Cli");
            List<Type> cliModules = new List<Type>();

            foreach (Type type in assembly.GetTypes()
                .Where(t => typeof(CliModuleBase).IsAssignableFrom(t))
                .Where(t => t != typeof(CliModuleBase)))
            {
                cliModules.Add(type);
            }
            return cliModules;
        }

        private void GenerateDocFileContent(Type cliType, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            string moduleName = cliType.Name.Replace("CliModule", "");
            MethodInfo[] moduleMethods = cliType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            List<Type> cliTypesToDescribe;

            docBuilder.AppendLine(@$"# {moduleName}");
            docBuilder.AppendLine();
            moduleName = moduleName.ToLower();
        // ToFix: GetType, ToString, Equals, GetHashCode force excluding below
            foreach (MethodInfo method in moduleMethods.Where(method => method.Name != "GetType" && method.Name != "ToString" && method.Name != "Equals" && method.Name != "GetHashCode"))
            {
                CliFunctionAttribute attributeFun = method.GetCustomAttribute<CliFunctionAttribute>();
                CliPropertyAttribute attributeProp = method.GetCustomAttribute<CliPropertyAttribute>();

                ParameterInfo[] parameters = method.GetParameters();
                cliTypesToDescribe = new List<Type>();
                
                StringBuilder methodArg = new StringBuilder();
                StringBuilder paramBuilder = new StringBuilder();
                
                if (parameters.Length == 0)
                {
                    paramBuilder.AppendLine("_None_");
                }
                else
                {
                    paramBuilder.AppendLine("| Parameter name | Type |");
                    paramBuilder.AppendLine("| :--- | :--- |");
                    methodArg.Append("(");
                    string cliParameterType;
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        cliParameterType = _sharedContent.GetTypeToWrite(parameter.ParameterType, cliTypesToDescribe);
                        paramBuilder.AppendLine($"| {parameter.Name} | `{cliParameterType}` |");
                        methodArg.Append($"{parameter.Name}, ");
                    }

                    methodArg.Remove(methodArg.Length - 2, 2);
                    methodArg.Append(")");
                }
                
                docBuilder.AppendLine(@$"## {moduleName}.{attributeFun?.FunctionName
                                                              ?? attributeProp?.PropertyName 
                                                              ?? method.Name.Substring(0,1).ToLower() + method.Name.Substring(1)
                    }{methodArg}");
                docBuilder.AppendLine();
                docBuilder.AppendLine(@$"{attributeFun?.Description ?? attributeProp?.Description ?? ""} ");
                docBuilder.AppendLine();
                _markdownGenerator.OpenTabs(docBuilder);
                _markdownGenerator.CreateTab(docBuilder, "Request");
                docBuilder.AppendLine(@"### **Parameters**");
                docBuilder.AppendLine();

                docBuilder.Append(paramBuilder);
                
                _markdownGenerator.CloseTab(docBuilder);
                docBuilder.AppendLine();
                _markdownGenerator.CreateTab(docBuilder, "Response");
                docBuilder.AppendLine(@$"### Return type");
                docBuilder.AppendLine();

                Type returnType = method.ReturnType;
                string returnRpcType = _sharedContent.GetTypeToWrite(returnType, cliTypesToDescribe);

                docBuilder.AppendLine(@$"`{returnRpcType}`");

                _markdownGenerator.CloseTab(docBuilder);

                if (cliTypesToDescribe.Count != 0)
                {
                    docBuilder.AppendLine();
                    _markdownGenerator.CreateTab(docBuilder, "Object definitions");
                    _sharedContent.AddObjectsDescription(docBuilder, cliTypesToDescribe);
                    _markdownGenerator.CloseTab(docBuilder);
                }

                _markdownGenerator.CloseTabs(docBuilder);
                docBuilder.AppendLine();

                if (attributeFun?.ExampleRequest != null || attributeFun?.ExampleResponse != null)
                {
                    docBuilder.AppendLine(@"### **Example**");
                    _markdownGenerator.OpenTabs(docBuilder);
                    _markdownGenerator.CreateTab(docBuilder, "Request");
                    _markdownGenerator.CreateCodeBlock(docBuilder, $"{attributeFun?.ExampleRequest ?? ""}");
                    _markdownGenerator.CloseTab(docBuilder);
                    _markdownGenerator.CreateTab(docBuilder, "Response");
                    _markdownGenerator.CreateCodeBlock(docBuilder, $"{attributeFun?.ExampleResponse ?? ""}");
                    _markdownGenerator.CloseTab(docBuilder);
                    _markdownGenerator.CloseTabs(docBuilder);
                    docBuilder.AppendLine();
                }
            }
            _sharedContent.Save(moduleName, docsDir + "/cli-modules", docBuilder);
        }
    }
}
