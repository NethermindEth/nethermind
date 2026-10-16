import sys
import xml.etree.ElementTree as ET
root=ET.parse(sys.argv[1]).getroot()
nodes={x.tag.rsplit("}",1)[-1]:x for x in root.iter()}
if nodes["version"].text!="11.8.1.174": raise SystemExit("Wrong NuGet version")
if nodes["repository"].get("commit")!="31b352993ea8678348286d50420713c05fff3ccf": raise SystemExit("Wrong package source commit")
print("Verified published package source commit")
