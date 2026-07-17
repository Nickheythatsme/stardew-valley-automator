using System.Text.Json;

namespace StardewAgent.Protocol;

public sealed record RequestEnvelope(
    string ProtocolVersion,
    string Type,
    string RequestId,
    string Method,
    JsonElement Params);

public sealed record ResponseEnvelope(
    string ProtocolVersion,
    string Type,
    string RequestId,
    bool Ok,
    object? Result = null,
    ProtocolError? Error = null)
{
    public static ResponseEnvelope Success(string requestId, object? result) =>
        new(ProtocolLimits.ProtocolVersion, "response", requestId, true, result);

    public static ResponseEnvelope Failure(string requestId, string code, string message, object? details = null) =>
        new(ProtocolLimits.ProtocolVersion, "response", requestId, false, Error: new(code, message, details));
}

public sealed record ProtocolError(string Code, string Message, object? Details = null);

public sealed record EventEnvelope(
    string ProtocolVersion,
    string Type,
    long Sequence,
    string Event,
    string? ExecutionId,
    object Payload);

public sealed record HelloParams(string Token, string ClientName, string ClientVersion);

public sealed record HelloResult(
    string ProtocolVersion,
    string SchemaVersion,
    string ModVersion,
    string GameVersion,
    string SmapiVersion,
    IReadOnlyList<string> Capabilities,
    string SessionId);

public sealed record ObserveParams(int? GridRadius = null);

public sealed record ExecutePlanParams(ActionPlan Plan);

public sealed record ExecutionIdParams(string ExecutionId);
