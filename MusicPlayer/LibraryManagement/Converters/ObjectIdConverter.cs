using MongoDB.Bson;
using Newtonsoft.Json;
using System;

public class ObjectIdConverter : JsonConverter<ObjectId>
{
    public override void WriteJson(JsonWriter writer, ObjectId value, JsonSerializer serializer)
    {
        // Serializes the ObjectId as a simple string
        writer.WriteValue(value.ToString());
    }

    public override ObjectId ReadJson(JsonReader reader, Type objectType, ObjectId existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        // Deserializes a string back into an ObjectId
        var value = (string)reader.Value;
        return value != null ? ObjectId.Parse(value) : ObjectId.Empty;
    }
}
