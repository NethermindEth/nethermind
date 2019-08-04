from locust import HttpLocust, TaskSet, task
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
        #for line in contents:
            #json_line = json.loads(line)
            self.client.post("/", json=json_line)


class WebsiteUser(HttpLocust):
    task_set = UserBehavior
    min_wait = 0
    max_wait = 0
    #host = "http://3.9.144.27:8545"
    host = "http://127.0.0.1:8545"
