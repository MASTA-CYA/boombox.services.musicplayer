using StackExchange.Redis;

namespace MusicServer
{
    public sealed class RedisCache
    {
        private static readonly Lazy<RedisCache> _instance = new Lazy<RedisCache>(() => new RedisCache());
        private static RedisCache Instance { get => _instance.Value; }
        private IDatabase Database { get; set; }
        private IServer Server { get; set; }
        private RedisCache()
        {
            var options = new ConfigurationOptions
            {
                //EndPoints = { "localhost" },
                EndPoints = { "10.0.0.254" },
                AbortOnConnectFail = true,
                SyncTimeout = 300000,
            };
            var redisConnection = ConnectionMultiplexer.Connect(options);
            Database = redisConnection.GetDatabase();
            Server = redisConnection.GetServer(redisConnection.GetEndPoints().First());
        }

        public static bool IsConnected(string key, out string error)
        {
            var isConnected = false;

            try
            {
                isConnected = Instance.Database.IsConnected(key);
                error = string.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return isConnected;
        }

        public static async Task<string> GetAsync(string key)
        {
            if (!Instance.Database.KeyExists(key))
                throw new Exception($"key: {key} does not exist");

            var value = await Instance.Database.StringGetAsync(key);

            if (string.IsNullOrWhiteSpace(value))
                throw new Exception($"Unable to find value for key: {key}");

            return value;
        }

        public static async Task<RedisValue[]> GetListAsync(string key)
        {
            RedisValue[] members = await Instance.Database.SetMembersAsync(key);

            if (members == null || members.Length == 0)
                throw new Exception($"Unable to find set for key: {key}");

            return members;
        }

        //public static async Task<List<string>> GetAllAsync(string keyPrefix)
        //{
        //    var keys = Instance.Server.KeysAsync(pattern: $"{keyPrefix}*");
        //    List<string> values = [];

        //    await Parallel.ForEachAsync(keys, async (key, token) =>
        //    {
        //        var value = await Instance.Database.StringGetAsync(key);
        //        if (string.IsNullOrWhiteSpace(value)) throw new Exception("Value not found");
        //        values.Add(value);
        //    });

        //    return values;
        //}

        public static async Task<bool> SetAsync(string key, string value)
        {
            bool success = await Instance.Database.StringSetAsync(key, value);

            if (!success)
                throw new Exception($"Failed to set values for key {key}");

            return success;
        }

        public static async Task<bool> SetListAsync(string key, RedisValue[] values)
        {
            long added = await Instance.Database.SetAddAsync(key, values);

            if (added <= 0)
                throw new Exception($"Failed to add set values for key {key}");

            return true;
        }

        public static async Task<bool> RemoveAsync(string key)
        {
            if (!await Instance.Database.KeyExistsAsync(key))
                throw new Exception($"Failed to find key: {key}");

            var success = await Instance.Database.KeyDeleteAsync(key);

            if (!success)
                throw new Exception($"Failed to remove values for key: {key}");

            return success;
        }
    }
}
