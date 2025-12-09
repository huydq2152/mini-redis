namespace MyRedis.Core
{
    public class ExpirationManager
    {
        // Min-Heap: Phần tử có thời gian hết hạn nhỏ nhất (sớm nhất) luôn ở đỉnh
        private readonly PriorityQueue<string, long> _ttlQueue = new PriorityQueue<string, long>();
        
        // Dictionary phụ để tra cứu nhanh xem key có TTL không (tránh scan Heap)
        private readonly Dictionary<string, long> _keyExpirations = new Dictionary<string, long>();

        // Set TTL cho một key
        public void SetExpiration(string key, long durationMs)
        {
            long expireAt = GetNow() + durationMs;
            
            // Lưu vào Dictionary để lookup O(1)
            _keyExpirations[key] = expireAt;
            
            // Đẩy vào Heap O(log N)
            // Lưu ý: Nếu key đã tồn tại trong Heap, ta cứ đẩy thêm vào (Duplicate).
            // Khi Pop ra, ta check lại với Dictionary, nếu không khớp (cũ) thì bỏ qua.
            // Đây là kỹ thuật "Lazy Update" để tránh việc phải tìm và xóa node giữa Heap (tốn O(N)).
            _ttlQueue.Enqueue(key, expireAt);
        }

        // Kiểm tra xem key đã hết hạn chưa (Dùng cho Lazy Expiration khi GET)
        public bool IsExpired(string key)
        {
            if (!_keyExpirations.TryGetValue(key, out long expireAt)) return false;

            if (GetNow() > expireAt)
            {
                // Đã hết hạn -> Xóa metadata và báo true
                _keyExpirations.Remove(key);
                return true;
            }
            return false;
        }

        public long? GetTTL(string key)
        {
            if (!_keyExpirations.TryGetValue(key, out long expireAt)) return null;
            long ttl = expireAt - GetNow();
            return ttl > 0 ? ttl : 0;
        }

        public void RemoveExpiration(string key)
        {
            _keyExpirations.Remove(key);
            // Không cần xóa trong Heap ngay, để nó tự trôi ra (Lazy)
        }

        // Trả về thời gian (ms) cho đến khi key tiếp theo hết hạn
        // Dùng để set timeout cho Socket.Select()
        public int GetNextTimeout()
        {
            if (_ttlQueue.Count == 0) return 10000; // Không có gì thì ngủ 10s

            if (_ttlQueue.TryPeek(out _, out long nextExpire))
            {
                long now = GetNow();
                if (nextExpire <= now) return 0; // Đã có cái hết hạn, dậy ngay!
                return (int)(nextExpire - now);
            }
            return 10000;
        }

        // Xóa các key đã hết hạn (Active Expiration)
        // Trả về danh sách key cần xóa khỏi Database chính
        public List<string> ProcessExpiredKeys()
        {
            var expiredKeys = new List<string>();
            long now = GetNow();
            int maxWork = 100; // Chỉ xóa tối đa 100 key mỗi lần để không block server (Throttling)

            while (_ttlQueue.Count > 0 && maxWork > 0)
            {
                // Xem đỉnh Heap
                if (_ttlQueue.TryPeek(out _, out long priority))
                {
                    if (priority > now) break; // Chưa đến giờ chết của ai cả
                }

                // Lấy ra
                string key = _ttlQueue.Dequeue();
                
                // Kiểm tra lại với Dictionary (để xử lý trường hợp Lazy Update hoặc đã bị xóa)
                if (_keyExpirations.TryGetValue(key, out long actualExpire))
                {
                    if (actualExpire <= now)
                    {
                        // Hết hạn thật
                        expiredKeys.Add(key);
                        _keyExpirations.Remove(key);
                    }
                    // Nếu actualExpire > now: Nghĩa là key này đã được set lại TTL mới,
                    // cái item vừa pop ra là rác cũ -> Bỏ qua.
                }
                
                maxWork--;
            }
            return expiredKeys;
        }

        private long GetNow() => Environment.TickCount64;
    }
}