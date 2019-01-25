[1mdiff --git a/src/Nethermind/Nethermind.sln b/src/Nethermind/Nethermind.sln[m
[1mindex 7ed20894..cb5355b4 100644[m
[1m--- a/src/Nethermind/Nethermind.sln[m
[1m+++ b/src/Nethermind/Nethermind.sln[m
[36m@@ -153,6 +153,10 @@[m [mProject("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Nethermind.Abi.Test", "Neth[m
 EndProject[m
 Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Nethermind.MrWolf", "Nethermind.MrWolf\Nethermind.MrWolf.csproj", "{43545FB4-82E6-4763-830B-D6B6E4662927}"[m
 EndProject[m
[32m+[m[32mProject("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Nethermind.Cli", "Nethermind.Cli\Nethermind.Cli.csproj", "{AABF177A-9E50-4736-AE65-0152B0C26121}"[m
[32m+[m[32mEndProject[m
[32m+[m[32mProject("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Nethermind.Facade", "Nethermind.Facade\Nethermind.Facade.csproj", "{B71A6E5F-73A4-4A45-A324-A10496F80DAA}"[m
[32m+[m[32mEndProject[m
 Global[m
 	GlobalSection(SolutionConfigurationPlatforms) = preSolution[m
 		Debug|Any CPU = Debug|Any CPU[m
[36m@@ -395,6 +399,14 @@[m [mGlobal[m
 		{43545FB4-82E6-4763-830B-D6B6E4662927}.Debug|Any CPU.Build.0 = Debug|Any CPU[m
 		{43545FB4-82E6-4763-830B-D6B6E4662927}.Release|Any CPU.ActiveCfg = Release|Any CPU[m
 		{43545FB4-82E6-4763-830B-D6B6E4662927}.Release|Any CPU.Build.0 = Release|Any CPU[m
[32m+[m		[32m{AABF177A-9E50-4736-AE65-0152B0C26121}.Debug|Any CPU.ActiveCfg = Debug|Any CPU[m
[32m+[m		[32m{AABF177A-9E50-4736-AE65-0152B0C26121}.Debug|Any CPU.Build.0 = Debug|Any CPU[m
[32m+[m		[32m{AABF177A-9E50-4736-AE65-0152B0C26121}.Release|Any CPU.ActiveCfg = Release|Any CPU[m
[32m+[m		[32m{AABF177A-9E50-4736-AE65-0152B0C26121}.Release|Any CPU.Build.0 = Release|Any CPU[m
[32m+[m		[32m{B71A6E5F-73A4-4A45-A324-A10496F80DAA}.Debug|Any CPU.ActiveCfg = Debug|Any CPU[m
[32m+[m		[32m{B71A6E5F-73A4-4A45-A324-A10496F80DAA}.Debug|Any CPU.Build.0 = Debug|Any CPU[m
[32m+[m		[32m{B71A6E5F-73A4-4A45-A324-A10496F80DAA}.Release|Any CPU.ActiveCfg = Release|Any CPU[m
[32m+[m		[32m{B71A6E5F-73A4-4A45-A324-A10496F80DAA}.Release|Any CPU.Build.0 = Release|Any CPU[m
 	EndGlobalSection[m
 	GlobalSection(SolutionProperties) = preSolution[m
 		HideSolutionNode = FALSE[m
