from locust import HttpLocust, TaskSet, task
import time
import json
import itertools


class UserBehavior(TaskSet):
    cont = itertools.count()

    @task(1)
    def trace_block(self):
        file = open('rpc_records', 'r')
        contents = file.readlines()
        for x in range(0, len(contents)):
            line = contents[x]
            if len(line) == 0:
                continue
            json_line = json.loads(line)
            #if "trace_block" in line:
            #    continue
            #if "trace_replayBlockTransactions" in line:
            #    continue
            #if "eth_call" in line:
            #    continue
            #if "eth_getTransactionReceipt" in line:
            #    continue
            #if "eth_getBlockByNumber" in line:
            #    continue
            #if "parity" in line:
            #    continue
            #if "eth_getCode" in line:
            #    continue
            #if "eth_blockNumber" in line:
            #    continue
            #if "eth_getBalance" in line:
            #    continue
        #for line in contents:
            #json_line = json.loads(line)
            self.client.post("/", json=json_line)
            #time.sleep(0.05)


class WebsiteUser(HttpLocust):
    task_set = UserBehavior
    min_wait = 0
    max_wait = 0
    #host = "http://3.9.144.27:8545"
    host = "http://127.0.0.1:8545"
