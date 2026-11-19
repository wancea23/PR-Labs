namespace CoffeeShop;

public sealed class Shop
{
    public static readonly (string Name, int Seconds)[] Products =
    {
        ("coffee", 4),
        ("tea", 3),
        ("croissant", 2),
    };

    public const int LowStock = 5;

    public static bool IsProduct(string name) => Array.Exists(Products, p => p.Name == name);

    public static int SecondsOf(string name) => Array.Find(Products, p => p.Name == name).Seconds;

    private readonly Dictionary<string, int> _stock = new();
    private readonly object _stockLock = new();
    private readonly string? _stockFile;

    public Shop(string? stockFile)
    {
        _stockFile = stockFile;
        if (stockFile != null && File.Exists(stockFile) && TryLoad(stockFile))
            return;
        foreach (var (name, _) in Products)
            _stock[name] = Random.Shared.Next(10, 51);
        Save();
    }

    public string Menu()
    {
        lock (_stockLock)
            return "MENU " + string.Join(" | ",
                Products.Select(p => $"{p.Name} ({p.Seconds} s): {_stock[p.Name]} left"));
    }

    public string StockText()
    {
        lock (_stockLock)
            return string.Join(", ", Products.Select(p => $"{p.Name} {_stock[p.Name]}"));
    }

    public TakeResult TryTake(IReadOnlyDictionary<string, int> items)
    {
        lock (_stockLock)
        {
            foreach (var (name, qty) in items)
                if (_stock[name] < qty)
                    return new TakeResult(false, name, _stock[name], new List<(string, int)>());

            var nowLow = new List<(string, int)>();
            foreach (var (name, qty) in items)
            {
                int before = _stock[name];
                _stock[name] = before - qty;
                if (before >= LowStock && _stock[name] < LowStock)
                    nowLow.Add((name, _stock[name]));
            }
            Save();
            return new TakeResult(true, null, 0, nowLow);
        }
    }

    public string Add(IReadOnlyDictionary<string, int> items)
    {
        lock (_stockLock)
        {
            foreach (var (name, qty) in items)
                _stock[name] += qty;
            Save();
            return string.Join(", ", Products.Select(p => $"{p.Name} {_stock[p.Name]}"));
        }
    }

    public List<(string Name, int Left)> LowProducts()
    {
        lock (_stockLock)
            return Products.Where(p => _stock[p.Name] < LowStock)
                           .Select(p => (p.Name, _stock[p.Name])).ToList();
    }

    private void Save()
    {
        if (_stockFile == null) return;
        try
        {
            string tmp = _stockFile + ".tmp";
            File.WriteAllLines(tmp, Products.Select(p => $"{p.Name} {_stock[p.Name]}"));
            File.Move(tmp, _stockFile, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write("server", $"could not save the stock: {e.Message}");
        }
    }

    private bool TryLoad(string file)
    {
        try
        {
            var loaded = new Dictionary<string, int>();
            foreach (string line in File.ReadAllLines(file))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && IsProduct(parts[0]) && int.TryParse(parts[1], out int n) && n >= 0)
                    loaded[parts[0]] = n;
            }
            if (loaded.Count != Products.Length) return false;
            foreach (var (k, v) in loaded) _stock[k] = v;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record TakeResult(bool Ok, string? Missing, int Left, List<(string Name, int Left)> NowLow);
