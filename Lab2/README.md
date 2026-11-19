# Lab2: A coffee shop server

A coffee shop server on TCP sockets, in C#. Clients order coffee, tea and croissants; the server takes
them from its stock, prepares the orders with one worker per CPU core and tells each client when its
order is ready. A provider brings new products and is alerted when a product runs low.

Only the `Socket` class is used (`Socket`, `Bind`, `Listen`, `Accept`, `Connect`, `Send`, `Receive`,
`Close`): no `TcpListener`, `TcpClient`, `NetworkStream` or any other library.

| Folder | Program |
|--------|---------|
| `Server/` | the server |
| `Client/` | the client and the provider: one program that starts in either role |
| `Shared/` | `LineFramer.cs`: cutting the byte stream into messages, used by both |
| `tests/` | test clients in Python, used for the runs in the report |

## Building and running

Requires the .NET 8 SDK or newer (it also runs on .NET 10). From the `Lab2` folder:

```
dotnet build -c Release Server
dotnet build -c Release Client
```

Start the server with its port, then any number of clients and one provider, each in its own terminal:

```
dotnet run -c Release --project Server -- 5000
dotnet run -c Release --project Client -- client 127.0.0.1 5000
dotnet run -c Release --project Client -- provider 127.0.0.1 5000
```

Between two computers in the same network, give the clients the IP address of the server's computer
(`ip addr` / `ipconfig`) instead of `127.0.0.1`, and allow the port in the firewall.

To keep the stock when the server restarts (bonus), give it a file:

```
dotnet run -c Release --project Server -- 5000 --stock stock.txt
```

Without the file, or the first time, the stock is a random amount from 10 to 50 of each product.

## Tests

The test clients in `tests/` connect to a running server with raw sockets (Python 3, no libraries):

| Script | What it checks |
|--------|----------------|
| `messages.py` | a message in pieces, two messages in one send, a message that is too long, random bytes, wrong commands |
| `many_clients.py` | more clients than workers: the extra orders wait in the queue |
| `last_croissant.py` | 12 clients order the last 3 croissants at the same moment |
| `deliver_and_order.py` | deliveries and orders at the same moment: no change is lost |
| `disappear.py` | a client closes during a preparation, another is reset while its order waits |

```
python3 tests/many_clients.py 127.0.0.1 5000
```

The report with all the results is `Lab2_IonMorosanu_FAF-242.pdf` (and `.docx`).

## Protocol

Every message, in both directions, is one line of UTF-8 text ending with `\n` (at most 1024 bytes from
a client). Commands are not case-sensitive. The first word of every answer says what it is.

| The client sends | The server answers |
|------------------|--------------------|
| `MENU` | `MENU coffee (4 s): 23 left \| tea (3 s): 41 left \| croissant (2 s): 12 left` |
| `ORDER 2 croissant 1 tea 1 coffee` | `ACCEPTED order 7: 2 croissant, 1 tea, 1 coffee, ready in 11 s` at once, and later `READY order 7: ...` |
| | or `REFUSED not enough croissant: only 1 left` |
| `PROVIDER` | `OK you are the provider. Stock: ...` (only one provider at a time) |
| `DELIVER 20 coffee 10 croissant` | `DELIVERED ... Stock now: ...` (provider only, otherwise `ERROR`) |
| `HELP` | `HELP ...` |
| `QUIT` | `BYE`, then the server closes the connection |

Sent by the server on its own: `WELCOME ...` on connect, `READY order N: ...` when an order is done, and
`ALERT croissant: only 4 left` to the provider when a product falls below 5. Every invalid input gets
`ERROR <reason>`: an unknown command, a missing or wrong parameter, an unknown product, a quantity that
is not a positive number, a message that is too long, or bytes that are not text.

The server logs every connection, order (accepted, refused, ready, cancelled), delivery, alert and error,
with the endpoint of the connection:

```
14:01:17.205 [127.0.0.1:48728] connected
14:01:17.246 [127.0.0.1:48728] order 1 accepted: 1 tea, 3 s, 0 orders before it in the queue
14:01:17.249 [127.0.0.1:48728] worker 1 prepares order 1 (1 tea, 3 s)
14:01:20.250 [127.0.0.1:48728] order 1 ready (worker 1)
```

## How it works

- **Threads.** The main thread only accepts. Every accepted connection gets its own thread, which receives
  and handles its commands. `Environment.ProcessorCount` worker threads prepare the orders.
- **Orders queue** (`Server/Orders.cs`). A client thread that accepts an order adds it to a FIFO queue
  (producer); a free worker takes the oldest order (consumer). Built on `Monitor.Wait` / `Monitor.Pulse`:
  a worker with nothing to do sleeps without using the CPU.
- **Stock** (`Server/Shop.cs`). Every read and change of the stock is inside one lock, so checking that
  there are enough products and taking them is one step. That's why two clients can't both get the last
  croissant, and why a delivery and an order at the same moment never lose a change.
- **Sending.** The client thread, the workers (`READY`) and other client threads (`ALERT`) can write to the
  same socket. Every connection has a send lock, so lines never mix. `Send` loops until every byte has gone.
- **A client disappears.** `Receive` returns 0 (the client closed) or throws (connection reset, or TCP
  keepalive found the network gone). The server then cancels all its orders: those still in the queue are
  removed and their products go back at once; a worker preparing one is waiting on the connection's
  cancellation handle, wakes up, stops and puts the products back. Whatever was prepared is lost.
- **The client** receives on one thread and reads the keyboard on the other, so `READY` and `ALERT` appear
  as soon as they arrive; the line being typed is redrawn under them. When the server disappears, it says
  so and exits.

## Questions for the defense

**Task 1**

1. The `LineFramer` keeps the bytes of an unfinished line in a buffer and returns a message only when its
   `\n` arrives. A message split over two `Receive` calls waits in the buffer for its second half; two
   messages in one `Receive` are both returned. A line longer than 1024 bytes gets an error and is skipped
   up to its `\n`.
2. Preparing takes seconds (11 s for 2 croissants, 1 tea and 1 coffee). Answering at once tells the client
   right away that the order is accepted, its number and its time, and the client can keep using the shop.
   Otherwise the client would see nothing for 11 seconds and could not tell a slow order from a dead server.

**Task 2**

1. The stock (`Shop`, one lock), the orders queue (`OrderQueue`, one lock plus `Monitor.Wait/Pulse`), each
   connection's socket for sending (one send lock per connection), the provider reference (`_providerLock`),
   the order counter (`Interlocked.Increment`) and the console log (a lock).
2. Without the lock: thread A reads croissant = 1, thread B reads croissant = 1, both see 1 ≥ 1, A writes 0,
   B writes 0. Both orders are accepted and two croissants are sold from a stock of one.
3. The client thread only receives and the worker only sends, so they don't disturb each other. But a
   worker and the client thread (or an alert) can send at the same time, so every send takes the
   connection's send lock and sends the whole line, and the worker must expect that the client may be gone.
4. One worker per core is the normal size for CPU work. Here the workers only wait (the preparation is
   simulated with a wait), so twice as many workers would prepare twice as many orders at once and orders
   would wait less in the queue. The shop would just have more "baristas" than the task asks for.

**Task 3**

1. The provider sends `PROVIDER` first. The server marks that connection (`IsProvider`) and keeps it in
   `_provider`; only one provider can be connected at a time.
2. Both `TryTake` and `Add` change the stock inside the same lock, so one runs completely before the other.
   Tested: 10 deliveries of 1 tea at the same moment as 3 orders of 1 tea changed the stock from 8 to 15.
3. The thread that made the stock fall below 5 sends it right after taking the products: the thread of the
   client that ordered. When the provider connects, its own thread sends the alerts for every product that
   is low at that moment.

**Task 4**

1. `Receive` returns 0 when the client closes the connection or its process is killed (the kernel closes
   the socket and sends FIN), and throws when the connection is reset. These are noticed at once. When the
   network is gone, nothing arrives at all: TCP keepalive notices it after about 20 s of silence, or a send
   fails.
2. Every connection has a cancellation token. The worker prepares by waiting on `Gone.WaitHandle` for the
   preparation time; when the client thread sees the client gone it cancels the token, the wait ends at
   once and the worker puts the products back with `Shop.Add`. Orders still in the queue are removed and
   put back by the client thread. Both use the queue's lock, so an order goes back exactly once.
3. Messages from the server (`READY`, `ALERT`) can arrive at any moment, and the user may be in the middle of
   typing. If one thread did both, a message would wait until the user pressed Enter. Here a receiver thread
   prints messages as they arrive, and the main thread reads the keys.

## Control questions

1. The listening socket is bound to the port and only accepts connections; every `Accept` returns a new
   socket for one client, used to talk to it. With five clients and the provider the server has one listening
   socket and six connected sockets.
2. TCP is a stream of bytes with no message boundaries: one send can arrive in pieces, and several sends can
   arrive together. The protocol ends every message with `\n`, and the framer cuts the stream at those.
3. 0 means the other side closed the connection normally (FIN): there will be no more data. A failure because
   the connection was reset (RST) means it ended abnormally, for example the process was killed or the
   connection was aborted. In both cases the server cleans up.
4. Producers: the client threads that accept orders. Consumers: the workers. Between them: the `OrderQueue`.
5. A thread that waits for a client uses no CPU, but it holds a stack (about 1 MB of reserved memory) and
   a kernel thread. 1000 clients would need 1000 threads. Asynchronous sockets
   (`ReceiveAsync`, or `select`/`epoll` underneath) would serve them with a few threads.
6. Two clients order the last croissant (see Task 2, question 2). Another example is a delivery and an order
   at the same moment, both reading tea = 8. The delivery writes 18 and the order writes 7, so the delivery
   is lost.
7. The four numbers: the client IP and port, and the server IP and port. Five clients on the same machine
   each get a different client port from the operating system, so all five connections to the same server
   port are different.
