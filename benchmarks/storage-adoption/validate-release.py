import sys
import xml.etree.ElementTree as ET
root=ET.parse(sys.argv[1]).getroot()
nodes={x.tag.rsplit("}",1)[-1]:x for x in root.iter()}
if nodes["version"].text!="11.8.1.171": raise SystemExit("Wrong NuGet version")
if nodes["repository"].get("commit")!="94e9fea4337b59d8b248cfae5c16e3ea18f7e2e2": raise SystemExit("Wrong package source commit")
print("Verified published package source commit")
