namespace AiControlCenter.Gateway.Operations;

public sealed class OperationsCenterOptions
{
    public const string SectionName = "OperationsCenter";

    public int RequestTimeoutSeconds { get; init; } = 3;
    public int FreshnessSeconds { get; init; } = 30;
    public string RabbitMqManagementAddress { get; init; } = "http://rabbitmq:15672";
    public Dictionary<string, OperationsServiceTargetOptions> Services { get; init; } = [];
    public string[] Queues { get; init; } = [];
}

public sealed class OperationsServiceTargetOptions
{
    public string Label { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
}
