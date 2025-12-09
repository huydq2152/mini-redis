namespace MyRedis.Storage.DataStructures;

public class SortedSet
{
    private readonly Dictionary<string, double> _dict = new Dictionary<string, double>();
    private readonly AvlTree _tree = new AvlTree();

    public bool Add(string key, double score)
    {
        // Logic đơn giản hóa: Nếu đã có thì không update (Redis thật có update)
        if (_dict.ContainsKey(key)) return false;

        _dict[key] = score;
        _tree.Add(key, score);
        return true;
    }

    public double? GetScore(string key)
    {
        if (_dict.TryGetValue(key, out double score)) return score;
        return null;
    }

    // Lấy danh sách theo thứ tự Score tăng dần
    public List<string> Range(int start, int stop)
    {
        // Xử lý chỉ số âm (giống Redis: -1 là phần tử cuối)
        int size = _tree.Root?.Size ?? 0;
        if (start < 0) start += size;
        if (stop < 0) stop += size;

        if (start < 0) start = 0;
        if (stop >= size) stop = size - 1;

        var resultNodes = new List<AvlNode>();
        if (start <= stop)
        {
            _tree.GetRange(_tree.Root, start, stop, resultNodes);
        }

        // Map từ Node sang String Key
        return resultNodes.Select(n => n.Key).ToList();
    }
}