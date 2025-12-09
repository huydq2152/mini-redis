namespace MyRedis.Storage.DataStructures;

public class AvlTree
{
    public AvlNode Root { get; private set; }

    // Hàm tiện ích lấy chiều cao và kích thước an toàn (tránh null) [cite: 1605-1610]
    private int GetHeight(AvlNode node) => node?.Height ?? 0;

    private int GetSize(AvlNode node) => node?.Size ?? 0;

    // Cập nhật lại Height và Size sau khi cây thay đổi [cite: 1615-1617]
    private void Update(AvlNode node)
    {
        if (node == null) return;
        node.Height = 1 + Math.Max(GetHeight(node.Left), GetHeight(node.Right));
        node.Size = 1 + GetSize(node.Left) + GetSize(node.Right);
    }

    // --- PHÉP XOAY CÂY (ROTATIONS) ---

    // Xoay phải (Khi lệch trái)
    private AvlNode RotateRight(AvlNode y)
    {
        AvlNode x = y.Left;
        AvlNode T2 = x.Right;

        // Xoay
        x.Right = y;
        y.Left = T2;

        // Cập nhật lại thông số (Lưu ý thứ tự: Con trước, Cha sau)
        Update(y);
        Update(x);

        return x; // Gốc mới
    }

    // Xoay trái (Khi lệch phải) [cite: 1620-1635]
    private AvlNode RotateLeft(AvlNode x)
    {
        AvlNode y = x.Right;
        AvlNode T2 = y.Left;

        // Xoay
        y.Left = x;
        x.Right = T2;

        // Cập nhật
        Update(x);
        Update(y);

        return y; // Gốc mới
    }

    // Cân bằng cây (Rebalancing) [cite: 1695-1718]
    private AvlNode Balance(AvlNode node)
    {
        Update(node); // Cập nhật thông số node hiện tại trước

        int balance = GetHeight(node.Left) - GetHeight(node.Right);

        // Case 1: Lệch Trái (Left Heavy)
        if (balance > 1)
        {
            // Kiểm tra xem con trái lệch đường nào (Left-Right Case)
            if (GetHeight(node.Left.Left) < GetHeight(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left);
            }

            return RotateRight(node);
        }

        // Case 2: Lệch Phải (Right Heavy)
        if (balance < -1)
        {
            // Right-Left Case
            if (GetHeight(node.Right.Right) < GetHeight(node.Right.Left))
            {
                node.Right = RotateRight(node.Right);
            }

            return RotateLeft(node);
        }

        return node; // Đã cân bằng
    }

    // --- HÀM THÊM (INSERT) ---

    public void Add(string key, double score)
    {
        Root = Insert(Root, key, score);
    }

    private AvlNode Insert(AvlNode node, string key, double score)
    {
        // 1. Chèn node như BST bình thường
        if (node == null) return new AvlNode(key, score);

        // So sánh: Ưu tiên Score, nếu bằng nhau thì so Key (Lexicographical)
        int compare = score.CompareTo(node.Score);
        if (compare == 0) compare = string.Compare(key, node.Key, StringComparison.Ordinal);

        if (compare < 0)
            node.Left = Insert(node.Left, key, score);
        else if (compare > 0)
            node.Right = Insert(node.Right, key, score);
        else
            return node; // Duplicate, không làm gì (hoặc update tùy logic)

        // 2. Cân bằng lại cây khi đệ quy quay về
        return Balance(node);
    }

    // --- TÍNH NĂNG "BIG TECH": TÌM KIẾM THEO RANK & RANGE ---

    // Lấy danh sách node trong khoảng Rank [start, stop] (0-based)
    // Đây chính là lệnh ZRANGE
    public void GetRange(AvlNode node, int start, int stop, List<AvlNode> result)
    {
        if (node == null) return;

        // Tính Rank của node hiện tại trong cây con này
        int leftSize = GetSize(node.Left);
        int currentRank = leftSize; // Rank bắt đầu từ 0

        // Nếu vùng cần lấy nằm bên trái -> Đệ quy trái
        if (start < currentRank)
        {
            GetRange(node.Left, start, stop, result);
        }

        // Nếu node hiện tại nằm trong vùng -> Lấy
        if (start <= currentRank && stop >= currentRank)
        {
            result.Add(node);
        }

        // Nếu vùng cần lấy nằm bên phải -> Đệ quy phải
        // Lưu ý: Khi sang phải, Rank tương đối bị trừ đi (currentRank + 1)
        if (stop > currentRank)
        {
            GetRange(node.Right, start - (currentRank + 1), stop - (currentRank + 1), result);
        }
    }
}