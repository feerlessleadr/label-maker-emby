using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LabelMaker.Core.Notifications
{
    /// <summary>
    /// Turns parsed JSON into plain dictionaries, lists, strings, numbers and booleans. Emby's own serializer, which sends websocket messages and
    /// route answers, handles those everywhere, which a raw <see cref="JsonElement"/> is not guaranteed to be.
    /// </summary>
    public static class PlainJson
    {
        public static object? ToPlain(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object?>();
                    foreach (var property in element.EnumerateObject()) dictionary[property.Name] = ToPlain(property.Value);
                    return dictionary;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(ToPlain).ToList();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
    }
}
