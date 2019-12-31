# Create the generated API files.
#
# This script uses nswag to create OAPI classes from the YAML specification.
#
# It creates files in Nethermind.BeaconNode.OApi and Nethermind.BeaconNode.OApi.Client with the suffix "-generated", that are not included in the build because they have issues.
#
# The contents of these files are copied into BeaconNodeOApi.cs and BeaconNodeOApiClient.cs, and the following issues fixed:
#
# * The genesis_time operation is generated in a separate, duplicate, implementation class. This does not compile (due to the duplicate class definitions). 
#   - To fix, copy just the Time methods (for the server and client) into the main class, and delete the rest of the duplicates.
#  
# * The integer uint64 format is not generated correctly; it is created as an int. This occurs in the parameter to GET block, the return result of GET genesis time, and in some data classes, e.g. the Slot value of BeaconBlockHeader.
#   - To fix, change the definitions to ulong (aka System.UInt64).
#
# * In BeaconNodeOApiClient.cs the parameter conversion of byte[] to string uses Base64 instead of prefixed hex.
#   - To fix, replace the section in ConvertToString() for byte[] with the correct encoding. 
#

dotnet restore # ensure the tool is installed
dotnet nswag openapi2cscontroller /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeOApi /namespace:Nethermind.BeaconNode.OApi /output:BeaconNodeOApi-generated.cs /UseLiquidTemplates:true /AspNetNamespace:"Microsoft.AspNetCore.Mvc" /ControllerBaseClass:"Microsoft.AspNetCore.Mvc.Controller"
dotnet nswag openapi2csclient /input:oapi/beacon-node-oapi.yaml /classname:BeaconNodeOApiClient /namespace:Nethermind.BeaconNode.OApiClient /ContractsNamespace:Nethermind.BeaconNode.OApiClient.Contracts /output:../Nethermind.BeaconNode.OApiClient/BeaconNodeOApiClient-generated.cs
