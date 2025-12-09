using MyRedis.Core;

Console.WriteLine("Starting MyRedis Server...");
var server = new RedisServer();
server.Run();