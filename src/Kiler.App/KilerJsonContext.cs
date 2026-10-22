using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiler.App;

/// <summary>JSON with source generation, because Native AOT has no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(InstanceFileContent))]
internal sealed partial class KilerJsonContext : JsonSerializerContext;
