from locust import HttpLocust, TaskSet, task

class UserBehavior(TaskSet):

    @task(1)
    def getBlockNumber(l):
        l.client.post("/", json={"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1})

class WebsiteUser(HttpLocust):
    task_set = UserBehavior
    min_wait = 0
    max_wait = 0
    host = "http://127.0.0.1:8545"
