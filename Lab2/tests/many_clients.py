import os
import threading
import time

from shop import Conn

n = int(os.environ.get("CLIENTS", "10"))
clients = [Conn() for _ in range(n)]
ready = [None] * n
start = time.time()
for c in clients:
    c.send("ORDER 1 tea")


def wait(i):
    accepted = clients[i].wait_for("ACCEPTED")
    clients[i].wait_for("READY")
    ready[i] = (time.time() - start, accepted)


threads = [threading.Thread(target=wait, args=(i,)) for i in range(n)]
for t in threads:
    t.start()
time.sleep(1)
print(f"{n} clients ordered 1 tea (3 s) each at the same time")
print("while they wait, client 1 asks:", clients[0].ask("MENU", "MENU"))
for t in threads:
    t.join()
for i, (t, accepted) in sorted(enumerate(ready), key=lambda x: x[1][0]):
    print(f"client {i + 1:2}: {accepted.split(':')[0]}, READY after {t:4.1f} s")
for c in clients:
    c.close()
