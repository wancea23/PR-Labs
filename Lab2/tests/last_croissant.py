import threading

from shop import Conn, stock

LEFT = 3
admin = Conn()
have = stock(admin)["croissant"]
if have > LEFT:
    admin.ask(f"ORDER {have - LEFT} croissant", "ACCEPTED")
print("croissants in stock:", stock(admin)["croissant"])

n = 12
clients = [Conn() for _ in range(n)]
barrier = threading.Barrier(n)
answers = [None] * n


def order(i):
    barrier.wait()
    answers[i] = clients[i].ask("ORDER 1 croissant")


threads = [threading.Thread(target=order, args=(i,)) for i in range(n)]
for t in threads:
    t.start()
for t in threads:
    t.join()
print(f"{n} clients ordered 1 croissant at the same moment:")
for i, a in enumerate(answers):
    print(f"  client {i + 1:2}: {a}")
accepted = sum(a.startswith("ACCEPTED") for a in answers)
print(f"accepted: {accepted}, refused: {n - accepted}, croissants in stock now: {stock(admin)['croissant']}")
for c in clients + [admin]:
    c.close()
