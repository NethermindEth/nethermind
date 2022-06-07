import json
import glob
import sys

results_name = glob.glob(sys.argv[1])
results = open(results_name[0], "rt")
res_obj = json.loads(results.read())
passed = []
failed = []

for test in res_obj["testCases"].keys():
    if res_obj["testCases"][test]["summaryResult"]["pass"]:
        passed.append(res_obj["testCases"][test]["name"])
    else:
        failed.append(res_obj["testCases"][test]["name"])

print(str(len(passed)) + " Tests Passed")
for test in passed:
    print(f"\u2705 {test}")
    
print("")
print(str(len(failed)) + " Tests Failed")
for test in failed:
    print(f"\u274C {test}")
    
if len(failed) > 0:
  exit(1)
 
