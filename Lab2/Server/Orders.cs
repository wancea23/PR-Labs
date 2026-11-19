namespace CoffeeShop;

public enum OrderState { Waiting, Preparing }

public sealed class Order
{
    public required int Id { get; init; }
    public required Connection Owner { get; init; }
    public required IReadOnlyDictionary<string, int> Items { get; init; }
    public required int Seconds { get; init; }
    public OrderState State { get; set; }

    public string ItemsText => string.Join(", ", Items.Select(i => $"{i.Value} {i.Key}"));
}

public sealed class OrderQueue
{
    private readonly LinkedList<Order> _orders = new();
    private readonly object _lock = new();

    public void Add(Order order)
    {
        lock (_lock)
        {
            order.State = OrderState.Waiting;
            _orders.AddLast(order);
            Monitor.Pulse(_lock);
        }
    }

    public Order Take()
    {
        lock (_lock)
        {
            while (_orders.Count == 0)
                Monitor.Wait(_lock);
            Order order = _orders.First!.Value;
            _orders.RemoveFirst();
            order.State = OrderState.Preparing;
            return order;
        }
    }

    public List<Order> RemoveAllOf(Connection owner)
    {
        lock (_lock)
        {
            var removed = new List<Order>();
            for (var node = _orders.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Owner == owner)
                {
                    removed.Add(node.Value);
                    _orders.Remove(node);
                }
                node = next;
            }
            return removed;
        }
    }

    public int Count
    {
        get { lock (_lock) return _orders.Count; }
    }
}
