import json
import sys
import time

sys.path.insert(0, __file__.rsplit("/", 2)[0] + "/python")
import lanchat

port = int(sys.argv[1]) if len(sys.argv) > 1 else 5050
seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 20


class Outsider(lanchat.Peer):
    def handle(self, data, addr):
        try:
            m = json.loads(data)
        except ValueError:
            m = {}
        if m.get("type") == "private" and m.get("to") == self.name:
            print(f"private datagram from {addr[0]}:{addr[1]}:")
            print(f"  {data.decode()}")
            print("  decrypt with my key:", end=" ")
            super().handle(data, addr)
            print("failed, the message cannot be read" if not self.seen else "ok")
            return
        super().handle(data, addr)


eve = Outsider("Eve", port, "a wrong guess", show=print)
if eve.join():
    time.sleep(seconds)
    eve.close()
