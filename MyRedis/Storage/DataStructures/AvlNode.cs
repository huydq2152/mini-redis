namespace MyRedis.Storage.DataStructures;

public class AvlNode
{
    // Dữ liệu nghiệp vụ
    public string Key { get; set; }
    public double Score { get; set; }

    // Metadata cho cây AVL
    public int Height { get; set; } = 1;
    public int Size { get; set; } = 1; // Số lượng node trong cây con (bao gồm chính nó)

    // Liên kết
    public AvlNode Left { get; set; }
    public AvlNode Right { get; set; }

    public AvlNode(string key, double score)
    {
        Key = key;
        Score = score;
    }
}