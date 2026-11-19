import os
import time

from shop import Conn

c = Conn()

print("one message sent in three pieces: b'ORD', b'ER 1 te', b'a\\n'")
c.send(b"ORD")
time.sleep(0.5)
c.send(b"ER 1 te")
time.sleep(0.5)
c.send(b"a\n")
print("  ", c.wait_for(""))

print("two messages in one send: b'MENU\\nHELP\\n'")
c.send(b"MENU\nHELP\n")
print("  ", c.wait_for(""))
print("  ", c.wait_for(""))

print("a message of 3000 bytes, then MENU")
c.send(b"x" * 3000 + b"\nMENU\n")
print("  ", c.wait_for(""))
print("  ", c.wait_for(""))

print("random bytes")
c.send(bytes([0xFF, 0xFE, 0x00, 0x13]) + os.urandom(40).replace(b"\n", b"") + b"\n")
print("  ", c.wait_for(""))

for bad in ["ORDER 1 pizza", "ORDER 0 tea", "ORDER -3 tea", "ORDER two tea", "ORDER 2",
            "ORDER", "MENU please", "DELIVER 5 tea", "BREW coffee"]:
    print(f"{bad!r:16} ->", c.ask(bad))

print("still working:", c.ask("MENU"))
c.close()
