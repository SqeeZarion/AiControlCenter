using AiControlCenter.ControlPlane.Domain;
using FluentValidation;

namespace AiControlCenter.ControlPlane.Application;

public sealed class ListAgentDefinitionsQueryValidator : AbstractValidator<ListAgentDefinitionsQuery>
{
    public ListAgentDefinitionsQueryValidator()
    {
        RuleFor(query => query.Status).Must(status => status is null || Enum.IsDefined(status.Value));
        RuleFor(query => query.DirectionId).Must(id => id is null || id != Guid.Empty);
        RuleFor(query => query.Search).MaximumLength(AgentDefinition.NameMaxLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListAgentDefinitionsQuery.MaximumPageSize);
    }
}

public sealed class CreateAgentDefinitionCommandValidator : AbstractValidator<CreateAgentDefinitionCommand>
{
    public CreateAgentDefinitionCommandValidator()
    {
        RuleFor(command => command.DirectionId).NotEmpty();
        AgentDefinitionValidationRules.AddContentRules(this);
    }
}

public sealed class UpdateAgentDefinitionCommandValidator : AbstractValidator<UpdateAgentDefinitionCommand>
{
    public UpdateAgentDefinitionCommandValidator()
    {
        RuleFor(command => command.DirectionId).NotEmpty();
        RuleFor(command => command.Version).GreaterThan(0u);
        AgentDefinitionValidationRules.AddContentRules(this);
    }
}

public sealed class ChangeAgentDefinitionStatusCommandValidator
    : AbstractValidator<ChangeAgentDefinitionStatusCommand>
{
    public ChangeAgentDefinitionStatusCommandValidator()
    {
        RuleFor(command => command.Status).IsInEnum();
        RuleFor(command => command.Version).GreaterThan(0u);
    }
}

public sealed class ArchiveAgentDefinitionCommandValidator : AbstractValidator<ArchiveAgentDefinitionCommand>
{
    public ArchiveAgentDefinitionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

public sealed class RestoreAgentDefinitionCommandValidator : AbstractValidator<RestoreAgentDefinitionCommand>
{
    public RestoreAgentDefinitionCommandValidator() => RuleFor(command => command.Version).GreaterThan(0u);
}

internal static class AgentDefinitionValidationRules
{
    public static void AddContentRules<T>(AbstractValidator<T> validator)
        where T : class
    {
        validator.RuleFor(command => GetName(command)).Must(BeValidName)
            .WithName("Name")
            .WithMessage($"Name must contain 2-{AgentDefinition.NameMaxLength} Unicode scalar values after normalization.");
        validator.RuleFor(command => GetCode(command)).Must(BeValidCode)
            .WithName("Code")
            .WithMessage($"Code must contain 2-{AgentDefinition.CodeMaxLength} letters or digits separated by spaces, underscores or hyphens.");
        validator.RuleFor(command => GetDescription(command)).Must(BeValidDescription)
            .WithName("Description")
            .WithMessage($"Description cannot exceed {AgentDefinition.DescriptionMaxLength} characters after trimming.");
        validator.RuleFor(command => GetStatus(command)).IsInEnum().WithName("Status");
        validator.RuleFor(command => GetExecutionType(command)).IsInEnum().WithName("ExecutionType");
        validator.RuleFor(command => command).Must(HaveValidAiConfiguration)
            .WithMessage("AI agents require a prompt, valid ordered model routes, budgets and one to ten attempts.");
    }

    private static string GetName<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Name,
        UpdateAgentDefinitionCommand update => update.Name,
        _ => string.Empty,
    };

    private static string GetCode<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Code,
        UpdateAgentDefinitionCommand update => update.Code,
        _ => string.Empty,
    };

    private static string? GetDescription<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Description,
        UpdateAgentDefinitionCommand update => update.Description,
        _ => null,
    };

    private static AgentDefinitionStatus GetStatus<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.Status,
        UpdateAgentDefinitionCommand update => update.Status,
        _ => (AgentDefinitionStatus)(-1),
    };

    private static AgentExecutionType GetExecutionType<T>(T command) => command switch
    {
        CreateAgentDefinitionCommand create => create.ExecutionType,
        UpdateAgentDefinitionCommand update => update.ExecutionType,
        _ => (AgentExecutionType)(-1),
    };

    private static bool HaveValidAiConfiguration<T>(T command)
    {
        var values = command switch
        {
            CreateAgentDefinitionCommand x => (x.ExecutionType, x.SystemPrompt, x.ModelRoutesJson, x.MaxInputTokens, x.MaxOutputTokens, x.MaxEstimatedCostUsd, x.MaxAttempts),
            UpdateAgentDefinitionCommand x => (x.ExecutionType, x.SystemPrompt, x.ModelRoutesJson, x.MaxInputTokens, x.MaxOutputTokens, x.MaxEstimatedCostUsd, x.MaxAttempts),
            _ => default,
        };
        if (values.ExecutionType == AgentExecutionType.Test) return true;
        if (string.IsNullOrWhiteSpace(values.SystemPrompt) || string.IsNullOrWhiteSpace(values.ModelRoutesJson)
            || values.ModelRoutesJson.Length > 65_536
            || values.MaxInputTokens is <= 0 || values.MaxOutputTokens is <= 0
            || values.MaxEstimatedCostUsd is <= 0 || values.MaxAttempts is < 1 or > 10) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(values.ModelRoutesJson);
            var routes = json.RootElement;
            if (routes.ValueKind != System.Text.Json.JsonValueKind.Array || routes.GetArrayLength() == 0) return false;
            if (routes.GetArrayLength() > 10) return false;
            var entries = routes.EnumerateArray().ToArray();
            var priorities = entries.Select(x => x.GetProperty("priority").GetInt32()).ToArray();
            var providers = new HashSet<string>(
                ["GitHubCopilot", "LocalCopilotCli", "OpenAi", "XAi", "DeterministicTest"],
                StringComparer.Ordinal);
            var fallbackReasons = new HashSet<string>(
                ["QuotaExceeded", "RateLimited", "TemporarilyUnavailable", "ModelUnavailable", "InsufficientFunds"],
                StringComparer.Ordinal);
            var routesValid = priorities.Distinct().Count() == priorities.Length && priorities.All(x => x > 0)
                && entries.All(x => IsValidRoute(x, providers, fallbackReasons, values.MaxEstimatedCostUsd!.Value));
            var routeKeys = entries.Select(entry => $"{entry.GetProperty("provider").GetString()}:{(entry.TryGetProperty("connectionName", out var connectionName) ? connectionName.GetString() : string.Empty)}:{entry.GetProperty("modelId").GetString()}")
                .ToArray();
            routesValid = routesValid
                && routeKeys.Distinct(StringComparer.Ordinal).Count() == routeKeys.Length
                && entries.Any(entry => !entry.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean())
                && entries.Sum(entry => entry.TryGetProperty("maxAttempts", out var attempts)
                    ? attempts.GetInt32() : 1) <= values.MaxAttempts;
            if (!routesValid) return false;
            var toolsJson = command switch
            {
                CreateAgentDefinitionCommand x => x.ToolPolicyJson,
                UpdateAgentDefinitionCommand x => x.ToolPolicyJson,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(toolsJson)) return true;
            using var tools = System.Text.Json.JsonDocument.Parse(toolsJson);
            var allowlist = new HashSet<string>(["system.utc-time", "text.length"], StringComparer.Ordinal);
            return tools.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                && tools.RootElement.EnumerateArray().All(x => x.ValueKind == System.Text.Json.JsonValueKind.String
                    && allowlist.Contains(x.GetString()!));
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    private static bool IsValidRoute(
        System.Text.Json.JsonElement route,
        HashSet<string> providers,
        HashSet<string> fallbackReasons,
        decimal runCostLimit)
    {
        if (route.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        var allowedProperties = new HashSet<string>(
            ["provider", "connectionId", "connectionName", "modelId", "priority", "enabled", "scenario",
                "allowedFallbackReasons", "maxAttempts", "timeoutSeconds", "paidExecutionAllowed",
                "paidFallbackAllowed", "safetyCapUsd", "dailyCostCapUsd", "monthlyCostCapUsd",
                "maxRequestsPerDay", "maxRequestsPerMonth"],
            StringComparer.Ordinal);
        if (route.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name))) return false;
        if (route.TryGetProperty("connectionName", out var connectionName)
            && (connectionName.ValueKind != System.Text.Json.JsonValueKind.String
                || connectionName.GetString() is not { Length: > 0 and <= ProviderConnectionDisplayNameMaxLength }))
            return false;
        var provider = route.GetProperty("provider").GetString() ?? string.Empty;
        if (!providers.Contains(provider) || route.GetProperty("modelId").GetString() is not { Length: > 0 and <= 128 })
            return false;
        var hasValidConnectionId = route.TryGetProperty("connectionId", out var connectionId)
            && connectionId.ValueKind == System.Text.Json.JsonValueKind.String
            && Guid.TryParse(connectionId.GetString(), out var parsedConnectionId)
            && parsedConnectionId != Guid.Empty;
        if (route.TryGetProperty("connectionId", out _) && !hasValidConnectionId)
            return false;
        if (provider == "LocalCopilotCli" && !hasValidConnectionId)
            return false;
        var maxAttempts = route.TryGetProperty("maxAttempts", out var attempts) ? attempts.GetInt32() : 1;
        var timeoutSeconds = route.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : 60;
        if (maxAttempts is < 1 or > 3 || timeoutSeconds is < 1 or > 300) return false;
        if (route.TryGetProperty("allowedFallbackReasons", out var reasons)
            && (reasons.ValueKind != System.Text.Json.JsonValueKind.Array
                || reasons.EnumerateArray().Any(reason => reason.ValueKind != System.Text.Json.JsonValueKind.String
                    || !fallbackReasons.Contains(reason.GetString()!))))
            return false;
        if (provider is not ("OpenAi" or "XAi"))
        {
            var paidExecution = route.TryGetProperty("paidExecutionAllowed", out var paidExecutionElement)
                && paidExecutionElement.GetBoolean();
            var paidFallback = route.TryGetProperty("paidFallbackAllowed", out var fallback)
                && fallback.GetBoolean();
            return !paidExecution && !paidFallback;
        }
        var paidExecutionAllowed = route.TryGetProperty("paidExecutionAllowed", out var paid) && paid.GetBoolean();
        var safetyCap = route.TryGetProperty("safetyCapUsd", out var cap) ? cap.GetDecimal() : 0;
        var dailyCap = route.TryGetProperty("dailyCostCapUsd", out var daily) ? daily.GetDecimal() : 0;
        var monthlyCap = route.TryGetProperty("monthlyCostCapUsd", out var monthly) ? monthly.GetDecimal() : 0;
        var dailyRequests = route.TryGetProperty("maxRequestsPerDay", out var dailyCount) ? dailyCount.GetInt32() : 0;
        var monthlyRequests = route.TryGetProperty("maxRequestsPerMonth", out var monthlyCount) ? monthlyCount.GetInt32() : 0;
        return paidExecutionAllowed && safetyCap > 0 && safetyCap <= runCostLimit
            && dailyCap >= safetyCap && monthlyCap >= dailyCap
            && dailyRequests is > 0 and <= 1_000 && monthlyRequests is > 0 and <= 10_000;
    }

    private const int ProviderConnectionDisplayNameMaxLength = 100;

    private static bool BeValidName(string? value)
    {
        try { _ = AgentDefinition.NormalizeName(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool BeValidCode(string? value)
    {
        try { _ = AgentDefinition.NormalizeCode(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool BeValidDescription(string? value)
    {
        try { _ = AgentDefinition.NormalizeDescription(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
