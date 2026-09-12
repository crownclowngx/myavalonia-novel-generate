using System.Security.Cryptography;
using System.Text.Json;
namespace NovelGeneratePlugin.Domain;

/// <summary>只做稳定指纹：对象属性排序，数组保留业务顺序。避免字典在重启后的散列顺序使已保存候选误报过期。</summary>
internal static class CanonicalJson
{
    public static string Hash<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) Write(writer, child); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
