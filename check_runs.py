import json, os
base = "/mnt/sda/expb-data/outputs/"
dirs = sorted([d for d in os.listdir(base) if "2sec" in d and "2026021" in d])
for d in dirs:
    path = os.path.join(base, d, "k6-summary.json")
    if os.path.exists(path):
        with open(path) as f:
            data = json.load(f)
        m = data.get("metrics", {})
        w = m.get("http_req_waiting", {})
        # Check structure
        if "values" in w:
            vals = w["values"]
        else:
            vals = w
        # Get status_200 from root_group
        rg = data.get("root_group", {}).get("groups", {})
        enp = rg.get("engine_newPayload", {}).get("checks", {}).get("status_200", {})
        passes = enp.get("passes", "?")
        fails = enp.get("fails", "?")
        med = vals.get("med", vals.get("median", 0))
        avg = vals.get("avg", vals.get("mean", 0))
        mn = vals.get("min", 0)
        mx = vals.get("max", 0)
        p90 = vals.get("p(90)", 0)
        p95 = vals.get("p(95)", 0)
        print(f"{d}")
        print(f"  pass={passes} fail={fails} med={med:.0f}ms avg={avg:.0f}ms min={mn:.0f}ms max={mx:.0f}ms p90={p90:.0f}ms p95={p95:.0f}ms")
    else:
        print(f"{d}: NO SUMMARY")
