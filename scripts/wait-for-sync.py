import os
import subprocess
import sys

network = os.getenv("NETWORK")

bad_logs = {"Exception": 1}
good_logs = {"Processed": 0}
required_count = {"Processed": 1000}

if network not in {"joc-mainnet", "joc-testnet", "linea-mainnet", "linea-sepolia", "energyweb", "volta"}:
    good_logs["Synced Chain Head"] = 0
    required_count["Synced Chain Head"] = 20

container_mapping = {
    "base-": "sedge-execution-op-l2-client",
    "world-": "sedge-execution-op-l2-client",
    "op-": "sedge-execution-op-l2-client",
    "taiko-": "sedge-execution-taiko-client",
}
default_container_name = "sedge-execution-client"

container_name = next(
    (name for prefix, name in container_mapping.items() if network.startswith(prefix)),
    default_container_name
)

process = subprocess.Popen(["docker", "logs", "-f", container_name], stdout=subprocess.PIPE, text=True)

found_bad_log = False
counter = 0

try:
    for line in process.stdout:
        print(line.strip())

        if found_bad_log:
            counter += 1
            if counter >= 100:
                print("Exiting after capturing extra logs due to error.")
                sys.exit(1)
            continue

        if any(bad_log in line for bad_log in bad_logs):
            print(f"Error: Found bad log in line: {line.strip()}")
            found_bad_log = True
            continue

        for good_log in good_logs:
            if good_log in line:
                good_logs[good_log] += 1

        if all(good_logs[log] >= required_count[log] for log in required_count):
            print("All required logs found.")
            sys.exit(0)

except Exception as e:
    print(f"An error occurred: {e}")
    sys.exit(1)
finally:
    process.terminate()

# Final exit if we did not reach required lines after an error
print("Unhandled termination. Probably critical issue in client. Stopping...")
sys.exit(1)
