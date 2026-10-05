import http from 'k6/http';
import exec from 'k6/execution';
import fs from 'k6/experimental/fs';
import csv from 'k6/experimental/csv';
import { group, check } from 'k6';

// --- Requests files ---
const requestsFilePath = __ENV.RPC_REQUESTS_FILE_PATH;
const requestsFile = await fs.open(requestsFilePath);
const requestsData = await csv.parse(requestsFile, {
  skipFirstLine: false,
});

// --- Test config file ---
const configFilePath = __ENV.RPC_CONFIG_FILE_PATH;
const configFile = open(configFilePath);
const config = JSON.parse(configFile);

export const options = config["options"]

export default async function () {
  const rpcEndpoint = __ENV.RPC_CLIENT_ENDPOINT;
  
  const idx = exec.scenario.iterationInTest;
  if (idx >= requestsData.length) {
    throw new Error("No more requests found");
  }

  const requestData = requestsData[idx];

  // const reqId = requestData[0];
  const reqName = requestData[1];
  const reqMethod = requestData[2];
  const payload = requestData[3];
  try {
    const headers = {
      "Content-Type": "application/json",
    };
    const tags = {
      "req_name": reqName ? reqName : reqMethod,
      "rpc_method": reqMethod,
    }

    group(reqName, function() {
      const response = http.post(rpcEndpoint, payload, {
        headers: headers,
        tags: tags,
      });
      // Checks
      check(response, {
        'status_200': (r) => r.status === 200,
        'has_result': (r) => {
          const data = r.json();
          return data !== undefined && data.result !== undefined && data.error === undefined;
        },
      }, tags);
    });
  } catch (e) {
    console.error(e);
  }
}
