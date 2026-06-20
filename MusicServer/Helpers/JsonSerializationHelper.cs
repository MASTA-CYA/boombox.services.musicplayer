using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text.Json;

namespace MusicServer.Helpers
{
    public static class JsonSerializationHelper
    {
        public static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Converts C# PascalCase property names to camelCase
        };

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
