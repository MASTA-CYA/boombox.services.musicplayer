
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MusicPlayer.Helpers
{
    public static class JsonSerializationHelper
    {
        public static readonly JsonSerializerSettings NamingSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            }
        };

        public static readonly JsonSerializerSettings FileSerializerSettings = new JsonSerializerSettings { Formatting = Formatting.Indented };
    }
}
