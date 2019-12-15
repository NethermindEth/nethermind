# create the generated API files

dotnet restore # ensure the tool is installed
dotnet nswag openapi2cscontroller /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeOApi /namespace:Nethermind.BeaconNode.OApi /output:BeaconNodeOApi-generated.cs /UseLiquidTemplates:true /AspNetNamespace:"Microsoft.AspNetCore.Mvc" /ControllerBaseClass:"Microsoft.AspNetCore.Mvc.Controller"
dotnet nswag openapi2csclient /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeOApiClient /namespace:Nethermind.BeaconNode.OApiClient /ContractsNamespace:Nethermind.BeaconNode.OApiClient.Contracts /output:../Nethermind.BeaconNode.OApiClient/BeaconNodeOApiClient-generated.cs
