namespace AiControlCenter.Contracts.V1;

//Це файл зі спільними контрактами повідомлень між сервісами.
//Він визначає, які дані передаються через RabbitMQ/MassTransit під час запуску агента та зміни його статусу.

public sealed record ExecuteTestAgentRunV1(
    Guid MessageId,
    //ID конфігурації агента, який було запущено.
    Guid RunId,
    Guid AgentId,
    string AgentName,
    //унікальний код;
    string AgentCode,
    long AgentVersion,
    Guid DirectionId,
    string DirectionName,
    string DirectionCode,
    string Input,
    TestRunOutcomeV1 ExpectedOutcome,
    string CorrelationId);

public enum TestRunOutcomeV1
{
    Succeed = 0,
    Fail = 1,
}

//Стан конкретного запуску змінився
public sealed record RunStatusChangedV1(
    Guid EventId,
    Guid RunId,
    Guid OwnerUserId,
    string Status,
    long Revision,
    DateTimeOffset OccurredAt,
    int? StepSequence,
    string? StepStatus);
