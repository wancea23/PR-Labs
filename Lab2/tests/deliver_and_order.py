import threading

from shop import Conn, stock

provider = Conn()
print(provider.ask("PROVIDER", "OK"))
provider.read_for(0.3)
clients = [Conn() for _ in range(5)]
before = stock(clients[0])["tea"]
print("tea before:", before)

barrier = threading.Barrier(1 + len(clients))


def deliver():
    barrier.wait()
    for _ in range(20):
        provider.send("DELIVER 1 tea")


accepted = []


def order(c):
    barrier.wait()
    for _ in range(2):
        if c.ask("ORDER 1 tea").startswith("ACCEPTED"):
            accepted.append(1)


threads = [threading.Thread(target=deliver)] + [threading.Thread(target=order, args=(c,)) for c in clients]
for t in threads:
    t.start()
for t in threads:
    t.join()
delivered = sum(line.startswith("DELIVERED") for line in provider.read_for(1))
after = stock(clients[0])["tea"]
print(f"at the same moment: {delivered} deliveries of 1 tea and {len(accepted)} accepted orders of 1 tea")
print(f"tea after: {after}, expected {before} + {delivered} - {len(accepted)} = {before + delivered - len(accepted)}")
for c in clients + [provider]:
    c.close()
