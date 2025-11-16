using System.Text.Json.Serialization;

namespace OpenSDK.NEL.Samse.Entities;

public class OpenSdkResponse<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("data")] public required T Data { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }
}