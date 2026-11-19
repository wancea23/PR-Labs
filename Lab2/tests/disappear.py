import socket
import struct
import time

from shop import Conn, stock

watcher = Conn()
print("stock:", stock(watcher))

a = Conn()
print("client A:", a.ask("ORDER 2 coffee", "ACCEPTED"))
time.sleep(2)
a.close()
print("client A closed its connection 2 s into the preparation")
time.sleep(0.5)
print("stock:", stock(watcher))

busy = [Conn() for _ in range(8)]
for c in busy:
    c.ask("ORDER 1 croissant", "ACCEPTED")
b = Conn()
print("all workers busy; client B:", b.ask("ORDER 3 tea", "ACCEPTED"))
b.sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
b.close()
print("client B was reset (RST) while its order waited in the queue")
time.sleep(0.5)
print("stock:", stock(watcher))
for c in busy:
    c.wait_for("READY")
    c.close()
watcher.close()
