using System.Threading.Channels;

namespace MyRedis.System;

public class BackgroundWorker
{
    // Channel unbounded: Chấp nhận nạp việc vào liên tục
    // SingleReader: Chỉ có 1 luồng xử lý nền (để tiết kiệm CPU core)
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );

    public BackgroundWorker()
    {
        // Khởi động luồng consumer ngay khi tạo Worker
        Task.Run(ProcessQueue);
    }

    // Producer: Luồng chính gọi hàm này để đẩy việc xuống
    // Thao tác này gần như O(1), cực nhanh.
    public void Submit(Action job)
    {
        _channel.Writer.TryWrite(job);
    }

    // Consumer: Chạy ngầm
    private async Task ProcessQueue()
    {
        var reader = _channel.Reader;
            
        // Vòng lặp chờ việc (Non-blocking wait)
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var job))
            {
                try
                {
                    // Thực thi công việc nặng
                    job.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BgWorker Error] {ex.Message}");
                }
            }
        }
    }
}