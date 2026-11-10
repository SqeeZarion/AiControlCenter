using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AiControlCenter.Grpc.Contracts.V1;

public static class CheckpointEnvelopeBudget
{
    public const int MaximumCheckpointUtf8Bytes = 65_536;

    public static int CalculateToolAuditBudget(
        string confirmedContext,
        string routeSnapshotJson,
        string providerAttemptReferencesJson,
        int checkpointVersion,
        int resumeCursor,
        DateTimeOffset timestamp)
    {
        if (checkpointVersion <= 0 || resumeCursor <= 0)
        {
            return 0;
        }

        JsonNode? routeSnapshot;
        JsonNode? attemptReferences;
        try
        {
            routeSnapshot = JsonNode.Parse(routeSnapshotJson);
            attemptReferences = JsonNode.Parse(providerAttemptReferencesJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }

        if (routeSnapshot is not JsonArray || attemptReferences is not JsonArray)
        {
            return 0;
        }

        for (var index = 0; index < ProviderCheckpointMetadataContract.MaximumNewAttemptReferencesPerCheckpoint; index++)
        {
            attemptReferences.AsArray().Add(ProviderCheckpointMetadataContract.CreateWorstCaseAttemptReference());
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["checkpointVersion"] = checkpointVersion,
            ["logicalStepId"] = "ai-execution",
            ["confirmedContext"] = confirmedContext,
            ["partialOutput"] = null,
            ["partialOutputIsComplete"] = false,
            ["completedToolCalls"] = new JsonArray(),
            ["providerAttemptReferences"] = attemptReferences,
            ["resumeCursor"] = resumeCursor,
            ["lastSafeTimestamp"] = timestamp.ToString("O"),
            ["routeSnapshot"] = routeSnapshot,
        };
        var payload = root.ToJsonString();
        root["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var envelopeBytes = Encoding.UTF8.GetByteCount(root.ToJsonString());
        return Math.Max(0, MaximumCheckpointUtf8Bytes - envelopeBytes);
    }
}

public static class ProviderCheckpointMetadataContract
{
    public const int ProviderRequestIdMaxUtf8Bytes = 256;
    public const int ActualModelMaxUtf8Bytes = 128;
    public const int ProviderNameMaxUtf8Bytes = 32;
    public const int FailureReasonMaxUtf8Bytes = 64;
    public const int AttemptStatusMaxUtf8Bytes = 64;
    public const int AttemptRecordStatusMaxUtf8Bytes = 32;
    public const int FinishStatusMaxUtf8Bytes = 32;
    public const int MetricSourceMaxUtf8Bytes = 24;
    public const int CorrelationIdMaxUtf8Bytes = 128;
    public const int IdempotencyKeyMaxUtf8Bytes = 160;
    public const int ErrorCodeMaxUtf8Bytes = 128;
    public const int OperationMaxUtf8Bytes = 64;
    public const int SafeExceptionTypeMaxUtf8Bytes = 128;
    public const int MaximumNewAttemptReferencesPerCheckpoint = 1;

    public static string? CanonicalizeProviderRequestId(string? value) =>
        CanonicalizeIdentifier(value, ProviderRequestIdMaxUtf8Bytes, "request");

    public static string? CanonicalizeActualModel(string? value) =>
        CanonicalizeIdentifier(value, ActualModelMaxUtf8Bytes, "model");

    public static string? CanonicalizeFailureReason(string? value) =>
        CanonicalizeIdentifier(value, FailureReasonMaxUtf8Bytes, "failure");

    public static string? CanonicalizeAttemptStatus(string? value) =>
        CanonicalizeIdentifier(value, AttemptStatusMaxUtf8Bytes, "status");

    public static string? CanonicalizeAttemptRecordStatus(string? value) =>
        CanonicalizeIdentifier(value, AttemptRecordStatusMaxUtf8Bytes, "status");

    public static string? CanonicalizeFinishStatus(string? value) =>
        CanonicalizeIdentifier(value, FinishStatusMaxUtf8Bytes, "finish");

    public static string? CanonicalizeMetricSource(string? value) =>
        CanonicalizeIdentifier(value, MetricSourceMaxUtf8Bytes, "source");

    public static string? CanonicalizeProviderName(string? value) =>
        CanonicalizeIdentifier(value, ProviderNameMaxUtf8Bytes, "provider");

    public static string? CanonicalizeCorrelationId(string? value) =>
        CanonicalizeIdentifier(value, CorrelationIdMaxUtf8Bytes, "correlation");

    public static string? CanonicalizeIdempotencyKey(string? value) =>
        CanonicalizeIdentifier(value, IdempotencyKeyMaxUtf8Bytes, "idempotency");

    public static string? CanonicalizeErrorCode(string? value) =>
        CanonicalizeIdentifier(value, ErrorCodeMaxUtf8Bytes, "error");

    public static string? CanonicalizeOperation(string? value) =>
        CanonicalizeIdentifier(value, OperationMaxUtf8Bytes, "operation");

    public static string? CanonicalizeSafeExceptionType(string? value) =>
        CanonicalizeIdentifier(value, SafeExceptionTypeMaxUtf8Bytes, "exception");

    public static string CanonicalizeGenerationDispatched(string? value) => value switch
    {
        "true" => "true",
        "false" => "false",
        _ => "unknown",
    };

    public static JsonObject CreateWorstCaseAttemptReference() => new()
    {
        ["Sequence"] = int.MaxValue,
        ["ProviderRequestId"] = new string('r', ProviderRequestIdMaxUtf8Bytes),
        ["ActualModel"] = new string('m', ActualModelMaxUtf8Bytes),
        ["Status"] = new string('s', AttemptStatusMaxUtf8Bytes),
    };

    private static string? CanonicalizeIdentifier(string? value, int maximumUtf8Bytes, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes && value.All(IsSafeIdentifierCharacter))
            return value;

        var fullFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var fingerprintLength = Math.Min(fullFingerprint.Length, maximumUtf8Bytes - kind.Length - 2);
        var fingerprint = fullFingerprint[..fingerprintLength];
        var fixedLength = kind.Length + fingerprint.Length + 2;
        var prefixLength = Math.Max(0, maximumUtf8Bytes - fixedLength);
        var safePrefix = new string(value.Where(IsSafeIdentifierCharacter).Take(prefixLength).ToArray());
        return safePrefix.Length == 0
            ? $"{kind}:{fingerprint}"
            : $"{kind}:{safePrefix}:{fingerprint}";
    }

    private static bool IsSafeIdentifierCharacter(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '-' or '_' or '.' or ':' or '/' or '@';
}
