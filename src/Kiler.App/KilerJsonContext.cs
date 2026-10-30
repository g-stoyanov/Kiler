using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiler.App;

/// <summary>JSON with source generation, because Native AOT has no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AdminLevelsResponse))]
[JsonSerializable(typeof(AdminUserResponse))]
[JsonSerializable(typeof(AdminUsersResponse))]
[JsonSerializable(typeof(DataResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(InstanceFileContent))]
[JsonSerializable(typeof(LevelRequest))]
[JsonSerializable(typeof(NewUserRequest))]
[JsonSerializable(typeof(PasswordRequest))]
[JsonSerializable(typeof(RenameRequest))]
[JsonSerializable(typeof(RightsRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(ShareFileContent))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(WizardRequest))]
[JsonSerializable(typeof(WizardResponse))]
internal sealed partial class KilerJsonContext : JsonSerializerContext;
