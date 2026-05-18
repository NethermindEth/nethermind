using NUnit.Framework;

// Default worker count for parallel test execution. Override at runtime by passing
// a .runsettings file:
//
//   <RunSettings><NUnit><NumberOfTestWorkers>N</NumberOfTestWorkers></NUnit></RunSettings>
//
// then `dotnet test ... --settings path/to.runsettings`. The runsettings value
// takes precedence over this assembly attribute.
[assembly: LevelOfParallelism(4)]
