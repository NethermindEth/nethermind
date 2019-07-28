from locust import HttpLocust, TaskSet, task
import json
import itertools

class UserBehavior(TaskSet):
    cont = itertools.count()

    @task(1)
    def getBlockNumber(l):
        l.client.post("/", json={"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1})

    @task(1)
    def traceBlock(l):
        data = {}
        data['jsonrpc'] = 2.0
        data['method'] = 'trace_replayBlockTransactions'
        blockno = next(UserBehavior.cont)
        data['params'] = [blockno, ["trace"]]
        data['id'] = blockno
        json_data = json.dumps(data)
        l.client.post("/", json_data)
#        l.client.post("/", json={"jsonrpc":"2.0","method":"trace_replayBlockTransactions","params":[1020897, ["trace"]],"id":67})

class WebsiteUser(HttpLocust):
    task_set = UserBehavior
    min_wait = 0
    max_wait = 0
    host = "http://127.0.0.1:8545"
