namespace MyRedis.Core
{
    public class IdleManager
    {
        private readonly LinkedList<Connection> _list = new LinkedList<Connection>();
        private const int IdleTimeoutMs = 300 * 1000; // 5 phút

        public void Add(Connection conn)
        {
            conn.LastActive = Environment.TickCount64;
            // Người mới nhất nằm ở CUỐI danh sách
            conn.Node = _list.AddLast(conn);
        }

        public void Touch(Connection conn)
        {
            conn.LastActive = Environment.TickCount64;
            if (conn.Node != null && conn.Node.List == _list)
            {
                // Move to end (O(1))
                _list.Remove(conn.Node);
                _list.AddLast(conn.Node);
            }
        }

        public void Remove(Connection conn)
        {
            if (conn.Node != null && conn.Node.List == _list)
            {
                _list.Remove(conn.Node);
                conn.Node = null;
            }
        }

        public List<Connection> GetIdleConnections()
        {
            var result = new List<Connection>();
            long now = Environment.TickCount64;

            // Kiểm tra từ ĐẦU danh sách (người cũ nhất)
            var node = _list.First;
            while (node != null)
            {
                var conn = node.Value;
                if (now - conn.LastActive > IdleTimeoutMs)
                {
                    result.Add(conn);
                    var next = node.Next;
                    _list.Remove(node); // O(1)
                    node = next;
                }
                else
                {
                    // Vì danh sách đã sắp xếp theo thời gian hoạt động,
                    // nếu gặp người chưa timeout thì tất cả người phía sau chắc chắn chưa timeout.
                    break; 
                }
            }
            return result;
        }
        
        // Tính thời gian ngủ tối ưu cho Select()
        public int GetNextTimeout()
        {
            if (_list.First == null) return 10000;
            
            long now = Environment.TickCount64;
            long deadline = _list.First.Value.LastActive + IdleTimeoutMs;
            
            if (deadline <= now) return 0;
            return (int)(deadline - now);
        }
    }
}