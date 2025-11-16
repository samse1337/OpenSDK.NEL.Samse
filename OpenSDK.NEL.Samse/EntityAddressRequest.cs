using System.Text.Json.Serialization;

namespace OpenSDK.NEL.Samse;

public class EntityAddressRequest
{
    [JsonPropertyName("item_id")] public string ItemId { get; set; } = string.Empty;
}