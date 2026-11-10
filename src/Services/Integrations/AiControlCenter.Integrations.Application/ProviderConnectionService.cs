using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.Application;

public sealed class ProviderConnectionService(IProviderConnectionRepository repository,
    IProviderExecutionReceiptRepository receipts, IProviderBudgetManager budgetManager,
    IIntegrationsUnitOfWork unitOfWork, IProviderCredentialProtector protector,
    IEnumerable<IAiProvider> providers, IToolExecutor toolExecutor,
    IProviderMetadataCanonicalizer metadataCanonicalizer, TimeProvider timeProvider)
{
    private readonly Dictionary<AiProviderKind, IAiProvider> providerMap = providers.ToDictionary(x => x.Kind);
    public async Task<IReadOnlyCollection<ProviderConnectionDto>> ListAsync(Guid owner, CancellationToken token) =>
        (await repository.ListAsync(owner, token)).Select(Map).ToArray();
    public async Task<ProviderConnectionDto> CreateAsync(Guid owner, AiProviderKind provider, string name,
        string? credential, CancellationToken token)
    {
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider), "Unknown provider type.");
        credential = Normalize(provider, credential);
        var connectionId = Guid.NewGuid();
        var protectionContext = new ProviderCredentialProtectionContext(provider, owner, connectionId, 1);
        var entity = ProviderConnection.Create(owner, provider, name,
            credential is null ? null : protector.ProtectCredential(credential, protectionContext),
            credential is null ? null : protector.Fingerprint(credential), timeProvider.GetUtcNow(), connectionId);
        repository.Add(entity); await unitOfWork.SaveChangesAsync(token); return Map(entity);
    }
    public Task<ProviderConnectionDto> CreateManualAsync(Guid owner, AiProviderKind provider, string name,
        string? credential, CancellationToken token)
    {
        if (provider is AiProviderKind.GitHubCopilot or AiProviderKind.GitHubCopilotLocalCli)
            throw GitHubConnectionFlowRequired();
        return CreateAsync(owner, provider, name, credential, token);
    }
    public async Task<ProviderConnectionDto> CreateOrReplaceAsync(Guid owner, AiProviderKind provider,
        string name, string credential, CancellationToken token)
    {
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider), "Unknown provider type.");
        var existing = await repository.GetByProviderAsync(owner, provider, name, token);
        if (existing is null) return await CreateAsync(owner, provider, name, credential, token);
        credential = Normalize(provider, credential) ?? throw new InvalidOperationException("Credential required.");
        existing.ReplaceCredential(protector.ProtectCredential(credential, CredentialContext(existing, existing.CredentialVersion + 1)),
            protector.Fingerprint(credential), timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(token);
        return Map(existing);
    }
    public async Task<ProviderConnectionDto> ReplaceCredentialAsync(Guid id, Guid owner, string credential, CancellationToken token)
    {
        var entity = await GetOwned(id, owner, true, token);
        if (entity.Provider is AiProviderKind.GitHubCopilot or AiProviderKind.GitHubCopilotLocalCli)
            throw GitHubConnectionFlowRequired();
        credential = Normalize(entity.Provider, credential) ?? throw new InvalidOperationException("Credential required.");
        entity.ReplaceCredential(protector.ProtectCredential(credential, CredentialContext(entity, entity.CredentialVersion + 1)),
            protector.Fingerprint(credential), timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(token); return Map(entity);
    }
    public async Task<ProviderConnectionDto> ConnectGitHubPatAsync(
        Guid owner,
        string personalAccessToken,
        string expectedGitHubLogin,
        CancellationToken token)
    {
        var credential = NormalizeGitHubFineGrainedPat(personalAccessToken);
        var expectedLogin = NormalizeGitHubLogin(expectedGitHubLogin);
        var validation = await Resolve(AiProviderKind.GitHubCopilot)
            .ValidateConnectionAsync(credential, token);

        if (validation.Kind != ProviderCredentialValidationKind.Valid)
            throw GitHubPatRejected(validation.Kind);
        if (validation.Models is not { Count: > 0 })
            throw new ProviderCredentialRejectedException("GitHubModelCatalogUnavailable",
                "GitHub не повернув доступний каталог моделей; PAT не збережено.");
        if (string.IsNullOrWhiteSpace(validation.ExternalAccountId)
            || string.IsNullOrWhiteSpace(validation.ExternalAccountLogin))
            throw new ProviderCredentialRejectedException("GitHubIdentityUnavailable",
                "GitHub не підтвердив власника credential; PAT не збережено.");
        if (!string.Equals(validation.ExternalAccountLogin, expectedLogin,
                StringComparison.OrdinalIgnoreCase))
            throw new ProviderCredentialRejectedException("GitHubAccountMismatch",
                "PAT належить іншому GitHub-акаунту; підключення не змінено.");

        var existing = await repository.GetByProviderAsync(owner, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", token);
        if (existing is not null
            && !string.IsNullOrWhiteSpace(existing.ExternalAccountId)
            && !string.Equals(existing.ExternalAccountId, validation.ExternalAccountId,
                StringComparison.Ordinal))
            throw new ProviderCredentialRejectedException("GitHubAccountMismatch",
                "PAT належить іншому GitHub-акаунту; підключення не змінено.");

        var now = timeProvider.GetUtcNow();
        if (existing is null)
        {
            var connectionId = Guid.NewGuid();
            existing = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot,
                "GitHub Copilot",
                protector.ProtectCredential(credential,
                    new ProviderCredentialProtectionContext(
                        AiProviderKind.GitHubCopilot, owner, connectionId, 1)),
                protector.Fingerprint(credential), now, connectionId);
            ApplyValidation(existing, validation, now);
            repository.Add(existing);
        }
        else
        {
            existing.ReplaceCredential(
                protector.ProtectCredential(credential,
                    CredentialContext(existing, existing.CredentialVersion + 1)),
                protector.Fingerprint(credential), now);
            ApplyValidation(existing, validation, now);
        }

        await unitOfWork.SaveChangesAsync(token);
        return Map(existing);
    }
    public async Task<ProviderConnectionDto> ConnectLocalCopilotCliAsync(
        Guid owner,
        CancellationToken token)
    {
        var validation = await ValidateLocalCopilotCliAsync(token);
        return await SynchronizeLocalCopilotCliAsync(owner, validation, token);
    }

    public async Task<ProviderCredentialValidationResult> ValidateLocalCopilotCliAsync(
        CancellationToken token)
    {
        var validation = await Resolve(AiProviderKind.GitHubCopilotLocalCli)
            .ValidateConnectionAsync(null, token);
        if (validation.Kind != ProviderCredentialValidationKind.Valid)
            throw new ProviderCredentialRejectedException(validation.Kind.ToString(),
                validation.Kind == ProviderCredentialValidationKind.ModelCatalogUnavailable
                    ? "GitHub-авторизацію завершено, але каталог моделей недоступний."
                    : "Local Copilot CLI runtime не готовий або не авторизований.");
        if (validation.Models is not { Count: > 0 })
            throw new ProviderCredentialRejectedException(
                ProviderCredentialValidationKind.ModelCatalogUnavailable.ToString(),
                "GitHub-авторизацію завершено, але каталог моделей порожній.");
        if (string.IsNullOrWhiteSpace(validation.ExternalAccountLogin)
            || string.IsNullOrWhiteSpace(validation.ExternalAccountId))
            throw new ProviderCredentialRejectedException("LocalCopilotIdentityUnavailable",
                "Local Copilot CLI не повернув підтверджений GitHub account.");
        return validation;
    }

    public async Task<ProviderConnectionDto> SynchronizeLocalCopilotCliAsync(
        Guid owner,
        ProviderCredentialValidationResult validation,
        CancellationToken token)
    {
        if (validation.Kind != ProviderCredentialValidationKind.Valid
            || validation.Models is not { Count: > 0 })
            throw new ProviderCredentialRejectedException(
                ProviderCredentialValidationKind.ModelCatalogUnavailable.ToString(),
                "GitHub-авторизацію завершено, але не вдалося оновити каталог моделей.");
        var now = timeProvider.GetUtcNow();
        var existing = await repository.GetByProviderAsync(owner,
            AiProviderKind.GitHubCopilotLocalCli, "Local Copilot CLI", token);
        if (existing is null)
        {
            existing = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilotLocalCli,
                "Local Copilot CLI", null, null, now);
            repository.Add(existing);
        }
        else if (existing.Status == ProviderConnectionStatus.Revoked)
        {
            existing.ReactivateWithoutCredential(now);
        }
        ApplyValidation(existing, validation, now);
        await unitOfWork.SaveChangesAsync(token);
        return Map(existing);
    }
    public async Task<ProviderConnectionDto> ValidateAsync(Guid id, Guid owner, CancellationToken token)
    {
        var entity = await GetOwned(id, owner, true, token);
        var provider = Resolve(entity.Provider);
        ProviderCredentialValidationResult validation;
        try
        {
            var credential = await PrepareCredentialAsync(entity, provider, token);
            validation = await provider.ValidateConnectionAsync(credential, token);
        }
        catch (UnauthorizedAccessException)
        {
            validation = new(entity.Provider == AiProviderKind.GitHubCopilot
                ? ProviderCredentialValidationKind.RequiresReconnect
                : ProviderCredentialValidationKind.InvalidCredential);
        }
        ApplyValidation(entity, validation, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(token);
        return Map(entity);
    }
    public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(Guid id, Guid owner, CancellationToken token)
    {
        var entity = await GetOwned(id, owner, true, token);
        if (entity.Status != ProviderConnectionStatus.Active) throw new InvalidOperationException("Connection is not active.");
        var provider = Resolve(entity.Provider);
        IReadOnlyCollection<ProviderModelDto> models;
        try
        {
            var credential = await PrepareCredentialAsync(entity, provider, token);
            models = await provider.ListModelsAsync(credential, token);
        }
        catch (UnauthorizedAccessException)
        {
            entity.MarkValidated(false, timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(token);
            throw new InvalidOperationException("Provider credential is invalid or expired.");
        }
        entity.SynchronizeModels(JsonSerializer.Serialize(models), timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(token);
        return models;
    }
    public async Task RevokeAsync(Guid id, Guid owner, CancellationToken token)
    { var entity = await GetOwned(id, owner, true, token); entity.Revoke(timeProvider.GetUtcNow()); await unitOfWork.SaveChangesAsync(token); }
    public async Task<ProviderUsageSummaryDto> GetUsageAsync(Guid owner, int days, CancellationToken token)
    {
        if (days is < 1 or > 366) throw new ArgumentOutOfRangeException(nameof(days));
        var since = timeProvider.GetUtcNow().AddDays(-days);
        var stored = await receipts.ListCompletedAsync(owner, since, token);
        var results = stored.Select(receipt => receipt.ResultJson is null ? null
                : JsonSerializer.Deserialize<AiExecutionResult>(receipt.ResultJson))
            .Where(result => result is not null).Cast<AiExecutionResult>().ToArray();
        return new(results.Sum(result => Math.Max(1, result.OutboundRequestCount)),
            results.Sum(result => result.InputTokens),
            results.Any(result => result.CachedInputTokens is not null)
                ? results.Sum(result => result.CachedInputTokens ?? 0) : null,
            results.Sum(result => result.OutputTokens),
            results.Sum(result => result.EstimatedCostUsd ?? 0), since,
            results.Any(result => result.UsageSource == ProviderMetricSource.ProviderReported)
                ? ProviderMetricSource.ProviderReported : ProviderMetricSource.Unavailable,
            results.Any(result => result.CostSource == ProviderMetricSource.Estimated)
                ? ProviderMetricSource.Estimated : ProviderMetricSource.Unavailable);
    }
    public async Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest request, CancellationToken token)
    {
        var existing = await receipts.GetAsync(request.OwnerUserId, request.IdempotencyKey, token);
        if (existing?.Status is ProviderExecutionReceiptStatus.Completed or ProviderExecutionReceiptStatus.Released
            && existing.ResultJson is not null)
            return JsonSerializer.Deserialize<AiExecutionResult>(existing.ResultJson)
                ?? throw new InvalidOperationException("Stored provider result is invalid.");
        if (existing is not null)
            return new(false, null, ProviderFailureKind.AmbiguousTimeout, "execution_in_progress_or_unknown",
                null, 0, 0, null, []);
        var entity = await GetOwned(request.ConnectionId, request.OwnerUserId, true, token);
        if (request.ExpectedProvider is not null && request.ExpectedProvider != entity.Provider)
            return new(false, null, ProviderFailureKind.Configuration,
                "provider_connection_mismatch", null, 0, 0, null, []);
        if (entity.Status != ProviderConnectionStatus.Active)
            return new(false, null, ProviderFailureKind.Authentication,
                entity.Status is ProviderConnectionStatus.RequiresReconnect
                    or ProviderConnectionStatus.Invalid
                    or ProviderConnectionStatus.Revoked
                    ? "connection_requires_reconnect"
                    : "connection_inactive",
                null, 0, 0, null, []);
        var provider = Resolve(entity.Provider);
        var paid = entity.Provider is AiProviderKind.OpenAi or AiProviderKind.XAi;
        if (paid && (request.ModelId.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            || ProviderPricingCatalog.Rates(entity.Provider, request.ModelId) is null))
            return new(false, null, ProviderFailureKind.LocalBudgetExceeded,
                "paid_model_price_unknown", null, 0, 0, null, []);
        AiExecutionResult result;
        ProviderExecutionReceipt? receipt = null;
        var outboundRequestCount = 0;
        try
        {
            var credential = await PrepareCredentialAsync(entity, provider, token);
            var selectedModel = request.ModelId;
            if (paid)
            {
                var reservation = await budgetManager.ReserveAsync(
                    request, entity.Provider, selectedModel, timeProvider.GetUtcNow(), token);
                if (!reservation.Allowed || reservation.Receipt is null)
                    return new(false, null, ProviderFailureKind.LocalBudgetExceeded,
                        reservation.ErrorCode ?? "local_budget_exceeded", null, 0, 0, null, []);
                receipt = reservation.Receipt;
            }
            if (entity.Provider == AiProviderKind.GitHubCopilotLocalCli)
            {
                // The SDK runtime is connection-oriented. Keep catalog/auth preflight and generation
                // in one provider-owned client session instead of opening back-to-back clients.
                selectedModel = request.ModelId.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                    ? "auto" : request.ModelId;
            }
            else
            {
                outboundRequestCount++;
                var models = await provider.ListModelsAsync(credential, token);
                selectedModel = request.ModelId.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                    ? models.FirstOrDefault()?.Id
                    : models.FirstOrDefault(model => model.Id.Equals(request.ModelId, StringComparison.Ordinal))?.Id;
            }
            if (selectedModel is null)
                result = new(false, null, ProviderFailureKind.ModelUnavailable, "model_not_available",
                    null, 0, 0, null, [], null);
            else if (!paid)
            {
                receipt = ProviderExecutionReceipt.Start(request.OwnerUserId, request.ConnectionId,
                    request.IdempotencyKey, timeProvider.GetUtcNow(), entity.Provider,
                    reservedRequestCount: 2 + Math.Clamp(request.MaxToolCycles, 0, 4));
                receipts.Add(receipt);
                await unitOfWork.SaveChangesAsync(token);
                result = await ExecuteProviderTurnsAsync(
                    provider, request with { ModelId = selectedModel }, credential,
                    outboundRequestCount, null, token);
            }
            else
            {
                async Task<bool> ReserveNextRequest(CancellationToken cancellationToken)
                {
                    var reservation = await budgetManager.ReserveAdditionalRequestAsync(
                        receipt!, request, timeProvider.GetUtcNow(), cancellationToken);
                    if (reservation.Allowed && reservation.Receipt is not null)
                        receipt = reservation.Receipt;
                    return reservation.Allowed;
                }
                result = await ExecuteProviderTurnsAsync(
                    provider, request with { ModelId = selectedModel }, credential,
                    outboundRequestCount, ReserveNextRequest, token);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            entity.MarkValidationFailed(ProviderConnectionStatus.RequiresReconnect,
                "Authentication", "Підключення потребує повторної авторизації.", timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            result = new(false, null, ProviderFailureKind.Authentication,
                "provider_authentication_failed", null, 0, 0, null, []);
        }
        catch (UnauthorizedAccessException)
        {
            entity.MarkValidationFailed(ProviderConnectionStatus.RequiresReconnect,
                "Authentication", "Підключення потребує повторної авторизації.", timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            result = new(false, null, ProviderFailureKind.Authentication,
                "provider_authentication_failed", null, 0, 0, null, []);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            result = new(false, null, ProviderFailureKind.RateLimited,
                "provider_rate_limited", null, 0, 0, null, []);
        }
        catch (HttpRequestException)
        {
            result = new(false, null, ProviderFailureKind.TemporarilyUnavailable,
                "provider_catalog_unavailable", null, 0, 0, null, [],
                OutboundRequestCount: outboundRequestCount);
        }
        catch (OperationCanceledException)
        {
            result = new(false, null,
                token.IsCancellationRequested ? ProviderFailureKind.Cancelled : ProviderFailureKind.TemporarilyUnavailable,
                token.IsCancellationRequested ? "cancelled" : "provider_catalog_unavailable",
                null, 0, 0, null, [], OutboundRequestCount: outboundRequestCount);
        }
        if (result.FailureKind == ProviderFailureKind.Authentication
            && entity.Status == ProviderConnectionStatus.Active)
        {
            entity.MarkValidationFailed(ProviderConnectionStatus.RequiresReconnect,
                "Authentication", "Підключення потребує повторної авторизації.",
                timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        result = metadataCanonicalizer.Canonicalize(result, request.ModelId);
        if (result.OutboundRequestCount < outboundRequestCount)
            result = result with { OutboundRequestCount = outboundRequestCount };
        if (receipt is not null)
        {
            if (paid)
                await budgetManager.ReconcileAsync(receipt, result, timeProvider.GetUtcNow(),
                    token.IsCancellationRequested ? CancellationToken.None : token);
            else
            {
                var resultJson = JsonSerializer.Serialize(result);
                if (result.FailureKind == ProviderFailureKind.AmbiguousTimeout)
                    receipt.MarkAmbiguous(resultJson, timeProvider.GetUtcNow(), result.OutboundRequestCount);
                else if (!result.Succeeded)
                    receipt.Release(resultJson, timeProvider.GetUtcNow(), result.OutboundRequestCount);
                else
                    receipt.Complete(resultJson, timeProvider.GetUtcNow(),
                        result.EstimatedCostUsd, result.OutboundRequestCount);
                await unitOfWork.SaveChangesAsync(token);
            }
        }
        return result;
    }

    public async Task<ResumeSafetyInspectionResult> InspectResumeSafetyAsync(
        Guid ownerUserId,
        AiProviderKind provider,
        string idempotencyKey,
        CancellationToken token)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("A valid owner id is required.", nameof(ownerUserId));
        if (provider != AiProviderKind.GitHubCopilotLocalCli)
            return new(true, "Allowed");
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Length > ProviderExecutionReceipt.IdempotencyKeyMaxLength)
            throw new ArgumentException("A valid idempotency key is required.", nameof(idempotencyKey));

        var receipt = await receipts.GetAsync(ownerUserId, idempotencyKey, token);
        if (receipt is null || receipt.Provider != provider || string.IsNullOrWhiteSpace(receipt.ResultJson))
            return new(false, "ResumeSafetyUnverifiable");
        if (receipt.Status is ProviderExecutionReceiptStatus.Pending
            or ProviderExecutionReceiptStatus.Ambiguous)
            return new(false, "AmbiguousOutcome");

        try
        {
            using var document = JsonDocument.Parse(receipt.ResultJson);
            var root = document.RootElement;
            var errorCode = ReadString(root, nameof(AiExecutionResult.ErrorCode));
            var hasOperationMarker = HasNonEmptyString(root, nameof(AiExecutionResult.Operation));
            var generationDispatched = ReadString(root, nameof(AiExecutionResult.GenerationDispatched));
            var hasGenerationMarker = HasProperty(root, nameof(AiExecutionResult.GenerationDispatched));

            if (string.Equals(errorCode, "local_copilot_cli_unavailable", StringComparison.Ordinal)
                && !hasOperationMarker && !hasGenerationMarker)
                return new(false, "LegacyAmbiguousOutcome");

            if (string.Equals(generationDispatched, "unknown", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(generationDispatched, "true", StringComparison.OrdinalIgnoreCase)
                    && !ReadBoolean(root, nameof(AiExecutionResult.Succeeded))))
                return new(false, "AmbiguousOutcome");
        }
        catch (JsonException)
        {
            return new(false, "ResumeSafetyUnverifiable");
        }

        return new(true, "Allowed");
    }

    private static bool HasProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool HasNonEmptyString(JsonElement root, string name) =>
        !string.IsNullOrWhiteSpace(ReadString(root, name));

    private static string? ReadString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static bool ReadBoolean(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return property.Value.GetBoolean();
        }
        return false;
    }
    private async Task<AiExecutionResult> ExecuteProviderTurnsAsync(
        IAiProvider provider,
        AiExecutionRequest request,
        string? credential,
        int outboundRequestCount,
        Func<CancellationToken, Task<bool>>? reserveNextRequest,
        CancellationToken token)
    {
        var definitions = SafeToolCatalog.Resolve(request.AllowedTools);
        var allowed = definitions.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var results = (request.PriorToolResults ?? []).ToList();
        var inputTokens = 0;
        var outputTokens = 0;
        var cachedTokens = 0;
        var cachedReported = false;
        decimal? totalCost = 0;
        string? partialOutput = null;
        for (var cycle = 0; cycle <= Math.Clamp(request.MaxToolCycles, 0, 4); cycle++)
        {
            if (reserveNextRequest is not null && !await reserveNextRequest(token))
                return new(false, null, ProviderFailureKind.LocalBudgetExceeded,
                    "local_budget_exceeded", null, inputTokens, outputTokens, totalCost, results,
                    CachedInputTokens: cachedReported ? cachedTokens : null,
                    PartialOutput: partialOutput, OutboundRequestCount: outboundRequestCount);
            AiExecutionResult turn;
            try
            {
                turn = metadataCanonicalizer.Canonicalize(await provider.ExecuteAsync(request with
                {
                    ToolDefinitions = definitions,
                    PriorToolResults = results,
                }, credential, token), request.ModelId);
                var providerRequestCount = provider.Kind == AiProviderKind.GitHubCopilotLocalCli
                    && turn.GenerationDispatched == "false"
                    ? turn.OutboundRequestCount
                    : Math.Max(1, turn.OutboundRequestCount);
                outboundRequestCount = checked(outboundRequestCount + providerRequestCount);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                return new(false, partialOutput, ProviderFailureKind.AmbiguousTimeout,
                    "provider_generation_outcome_ambiguous", request.IdempotencyKey,
                    inputTokens, outputTokens, totalCost, results,
                    CachedInputTokens: cachedReported ? cachedTokens : null,
                    FinishStatus: "Unknown", PartialOutput: partialOutput,
                    OutboundRequestCount: checked(outboundRequestCount + 1),
                    Operation: "Unknown", GenerationDispatched: "unknown",
                    SafeExceptionType: exception is HttpRequestException
                        ? nameof(HttpRequestException) : nameof(OperationCanceledException));
            }
            inputTokens += turn.InputTokens;
            outputTokens += turn.OutputTokens;
            if (turn.CachedInputTokens is int cached)
            {
                cachedTokens += cached;
                cachedReported = true;
            }
            totalCost = totalCost is null || turn.EstimatedCostUsd is null
                ? null : totalCost + turn.EstimatedCostUsd;
            partialOutput = turn.PartialOutput ?? partialOutput;
            var calls = turn.RequestedToolCalls ?? [];
            if (calls.Count == 0)
                return turn with
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedInputTokens = cachedReported ? cachedTokens : null,
                    EstimatedCostUsd = totalCost,
                    ToolInvocations = results,
                    PartialOutput = partialOutput,
                    OutboundRequestCount = outboundRequestCount,
                };
            if (cycle == request.MaxToolCycles || calls.Count > 10)
                return ToolFailure("tool_cycle_limit_exceeded", inputTokens, outputTokens,
                    cachedReported ? cachedTokens : null, totalCost, results, partialOutput,
                    outboundRequestCount);
            foreach (var call in calls)
            {
                if (!allowed.Contains(call.ToolName)
                    || call.CallId.Length is < 1 or > 160
                    || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 4096)
                    return ToolFailure("tool_not_allowed", inputTokens, outputTokens,
                        cachedReported ? cachedTokens : null, totalCost, results, partialOutput,
                        outboundRequestCount);
                if (results.Any(existing => existing.CallId == call.CallId)) continue;
                if (!HasToolAuditCapacity(request, results, call, partialOutput))
                    return ToolFailure("tool_audit_budget_exceeded", inputTokens, outputTokens,
                        cachedReported ? cachedTokens : null, totalCost, results, partialOutput,
                        outboundRequestCount);
                var invocation = await toolExecutor.ExecuteAsync(call,
                    $"{request.IdempotencyKey}:tool:{call.CallId}", token);
                results.Add(invocation);
                if (!invocation.Succeeded)
                    return ToolFailure(invocation.Error ?? "tool_failed", inputTokens, outputTokens,
                        cachedReported ? cachedTokens : null, totalCost, results, partialOutput,
                        outboundRequestCount);
            }
        }
        return ToolFailure("tool_cycle_limit_exceeded", inputTokens, outputTokens,
            cachedReported ? cachedTokens : null, totalCost, results, partialOutput,
            outboundRequestCount);
    }
    private static AiExecutionResult ToolFailure(string code, int inputTokens, int outputTokens,
        int? cachedTokens, decimal? cost, IReadOnlyCollection<ToolInvocationResult> results,
        string? partialOutput, int outboundRequestCount) =>
        new(false, null, ProviderFailureKind.Tool, code, null, inputTokens, outputTokens,
            cost, results, CachedInputTokens: cachedTokens, FinishStatus: "ToolFailed",
            PartialOutput: partialOutput, OutboundRequestCount: outboundRequestCount);

    private bool HasToolAuditCapacity(
        AiExecutionRequest request,
        IReadOnlyCollection<ToolInvocationResult> completed,
        ToolCallRequest next,
        string? partialOutput) => ToolCheckpointAuditSizer.Fits(
            request, completed, next, partialOutput, request.CheckpointToolAuditBudgetBytes,
            metadataCanonicalizer.MaximumCheckpointUtf8Bytes);
    public async Task<(string Json, bool AllAvailable)> ResolveRoutesAsync(
        Guid ownerUserId,
        string logicalRoutesJson,
        CancellationToken token)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        if (string.IsNullOrWhiteSpace(logicalRoutesJson) || logicalRoutesJson.Length > 65_536)
            throw new ArgumentException("Logical routes are invalid.", nameof(logicalRoutesJson));
        using var document = ParseLogicalRoutes(logicalRoutesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() is < 1 or > 10)
            throw new ArgumentException("Logical routes must contain one to ten items.", nameof(logicalRoutesJson));
        foreach (var route in document.RootElement.EnumerateArray())
        {
            if (route.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each logical route must be an object.", nameof(logicalRoutesJson));
            ValidateLogicalRoute(route);
        }
        var owned = await repository.ListAsync(ownerUserId, token);
        var resolved = new List<Dictionary<string, object?>>(document.RootElement.GetArrayLength());
        var allAvailable = true;
        foreach (var route in document.RootElement.EnumerateArray())
        {
            if (route.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each logical route must be an object.", nameof(logicalRoutesJson));
            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "provider", "connectionId", "connectionName", "modelId", "priority", "enabled", "scenario",
                "allowedFallbackReasons", "maxAttempts", "timeoutSeconds", "paidExecutionAllowed",
                "paidFallbackAllowed", "safetyCapUsd", "dailyCostCapUsd", "monthlyCostCapUsd",
                "maxRequestsPerDay", "maxRequestsPerMonth",
            };
            if (route.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)))
                throw new ArgumentException("Logical route contains an unknown property.", nameof(logicalRoutesJson));
            ValidateLogicalRoute(route);
            var providerName = RequiredString(route, "provider", 32);
            var requiredProvider = ParseLogicalProvider(providerName, nameof(logicalRoutesJson));
            var connectionName = route.TryGetProperty("connectionName", out var nameElement)
                ? nameElement.GetString()?.Trim() : null;
            Guid? requestedConnectionId = null;
            if (route.TryGetProperty("connectionId", out var connectionIdElement))
            {
                if (connectionIdElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(connectionIdElement.GetString(), out var parsedConnectionId)
                    || parsedConnectionId == Guid.Empty)
                    throw new ArgumentException("Route connection id is invalid.", nameof(logicalRoutesJson));
                requestedConnectionId = parsedConnectionId;
            }
            var providerAvailable = providerMap.TryGetValue(requiredProvider, out var adapter)
                && adapter.IsAvailableForRouting;
            var candidates = owned.Where(connection => connection.Provider == requiredProvider
                && connection.Status == ProviderConnectionStatus.Active
                && (requestedConnectionId is null || connection.Id == requestedConnectionId)
                && (connectionName is null || connection.DisplayName == connectionName)).ToArray();
            var connection = candidates.Length == 1 ? candidates[0] : null;
            var modelId = RequiredString(route, "modelId", 128);
            var modelAvailable = connection is not null && IsModelAvailable(connection, modelId);
            if (connection is not null && requiredProvider == AiProviderKind.GitHubCopilotLocalCli)
            {
                var validation = await adapter!.ValidateConnectionAsync(null, token);
                providerAvailable = validation.Kind == ProviderCredentialValidationKind.Valid;
                modelAvailable = providerAvailable && (modelId.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                    || validation.Models?.Any(model => model.Id.Equals(modelId, StringComparison.Ordinal)) == true);
            }
            if (!providerAvailable || !modelAvailable)
                connection = null;
            allAvailable &= connection is not null;
            var isPaid = requiredProvider is AiProviderKind.OpenAi or AiProviderKind.XAi;
            var pricingKnown = !isPaid || ProviderPricingCatalog.Rates(requiredProvider, modelId) is not null;
            var item = route.EnumerateObject().ToDictionary(property => property.Name,
                property => JsonSerializer.Deserialize<object?>(property.Value.GetRawText()));
            item.Remove("connectionName");
            item["ownerUserId"] = ownerUserId;
            item["connectionId"] = connection?.Id ?? Guid.Empty;
            item["provider"] = (connection?.Provider ?? requiredProvider).ToString();
            item["connectionAvailable"] = connection is not null;
            item["isPaid"] = isPaid;
            item["pricingKnown"] = pricingKnown;
            resolved.Add(item);
        }
        return (JsonSerializer.Serialize(resolved), allAvailable);
    }

    private static JsonDocument ParseLogicalRoutes(string logicalRoutesJson)
    {
        try
        {
            return JsonDocument.Parse(logicalRoutesJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Logical routes JSON is malformed.", nameof(logicalRoutesJson), exception);
        }
    }

    private static void ValidateLogicalRoute(JsonElement route)
    {
        var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "provider", "connectionId", "connectionName", "modelId", "priority", "enabled", "scenario",
            "allowedFallbackReasons", "maxAttempts", "timeoutSeconds", "paidExecutionAllowed",
            "paidFallbackAllowed", "safetyCapUsd", "dailyCostCapUsd", "monthlyCostCapUsd",
            "maxRequestsPerDay", "maxRequestsPerMonth",
        };
        if (route.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)))
            InvalidRoute();
        _ = RequiredString(route, "provider", 32);
        _ = RequiredString(route, "modelId", 128);
        _ = RequiredInteger(route, "priority", 1, int.MaxValue);
        OptionalString(route, "connectionName", 100);
        OptionalGuid(route, "connectionId");
        OptionalString(route, "scenario", 128);
        OptionalBoolean(route, "enabled");
        OptionalBoolean(route, "paidExecutionAllowed");
        OptionalBoolean(route, "paidFallbackAllowed");
        OptionalInteger(route, "maxAttempts", 1, 3);
        OptionalInteger(route, "timeoutSeconds", 1, 300);
        OptionalInteger(route, "maxRequestsPerDay", 1, 1_000);
        OptionalInteger(route, "maxRequestsPerMonth", 1, 10_000);
        OptionalPositiveDecimal(route, "safetyCapUsd");
        OptionalPositiveDecimal(route, "dailyCostCapUsd");
        OptionalPositiveDecimal(route, "monthlyCostCapUsd");
        if (route.TryGetProperty("allowedFallbackReasons", out var reasons))
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "QuotaExceeded", "RateLimited", "TemporarilyUnavailable",
                "ModelUnavailable", "InsufficientFunds",
            };
            if (reasons.ValueKind != JsonValueKind.Array || reasons.GetArrayLength() > 8
                || reasons.EnumerateArray().Any(reason => reason.ValueKind != JsonValueKind.String
                    || !allowed.Contains(reason.GetString()!)))
                InvalidRoute();
        }
    }

    private static string RequiredString(JsonElement route, string name, int maximumLength)
    {
        if (!route.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            InvalidRoute();
        var text = value.GetString()?.Trim() ?? string.Empty;
        if (text.Length is 0 || text.Length > maximumLength) InvalidRoute();
        return text;
    }

    private static int RequiredInteger(JsonElement route, string name, int minimum, int maximum)
    {
        if (!route.TryGetProperty(name, out var value)) InvalidRoute();
        if (value.ValueKind != JsonValueKind.Number) InvalidRoute();
        if (!value.TryGetInt32(out var number)) InvalidRoute();
        if (number < minimum || number > maximum) InvalidRoute();
        return number;
    }

    private static void OptionalString(JsonElement route, string name, int maximumLength)
    {
        if (route.TryGetProperty(name, out var value)
            && (value.ValueKind != JsonValueKind.String
                || value.GetString()?.Trim() is not { Length: > 0 } text
                || text.Length > maximumLength))
            InvalidRoute();
    }

    private static void OptionalGuid(JsonElement route, string name)
    {
        if (route.TryGetProperty(name, out var value)
            && (value.ValueKind != JsonValueKind.String
                || !Guid.TryParse(value.GetString(), out var parsed)
                || parsed == Guid.Empty))
            InvalidRoute();
    }

    private static AiProviderKind ParseLogicalProvider(string providerName, string parameterName)
    {
        if (string.Equals(providerName, "LocalCopilotCli", StringComparison.Ordinal))
            return AiProviderKind.GitHubCopilotLocalCli;
        if (Enum.TryParse<AiProviderKind>(providerName, false, out var provider)
            && Enum.IsDefined(provider))
            return provider;
        throw new ArgumentException("Route provider is invalid.", parameterName);
    }

    private static bool IsModelAvailable(ProviderConnection connection, string modelId)
    {
        if (connection.Provider == AiProviderKind.DeterministicTest
            && string.Equals(modelId, "deterministic-v1", StringComparison.Ordinal))
            return true;
        if (string.Equals(modelId, "Auto", StringComparison.Ordinal))
            return !string.IsNullOrWhiteSpace(connection.AvailableModelsJson);
        if (string.IsNullOrWhiteSpace(connection.AvailableModelsJson))
            return false;
        try
        {
            using var catalog = JsonDocument.Parse(connection.AvailableModelsJson);
            return catalog.RootElement.ValueKind == JsonValueKind.Array
                && catalog.RootElement.EnumerateArray().Any(model =>
                    model.TryGetProperty("Id", out var pascalId)
                        && string.Equals(pascalId.GetString(), modelId, StringComparison.Ordinal)
                    || model.TryGetProperty("id", out var camelId)
                        && string.Equals(camelId.GetString(), modelId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void OptionalBoolean(JsonElement route, string name)
    {
        if (route.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            InvalidRoute();
    }

    private static void OptionalInteger(JsonElement route, string name, int minimum, int maximum)
    {
        if (route.TryGetProperty(name, out _)) _ = RequiredInteger(route, name, minimum, maximum);
    }

    private static void OptionalPositiveDecimal(JsonElement route, string name)
    {
        if (route.TryGetProperty(name, out var value)
            && (value.ValueKind != JsonValueKind.Number
                || !value.TryGetDecimal(out var number) || number <= 0))
            InvalidRoute();
    }

    [DoesNotReturn]
    private static void InvalidRoute() => throw new ArgumentException(
        "Logical route has an invalid structure.", "logicalRoutesJson");
    private IAiProvider Resolve(AiProviderKind kind) => providerMap.TryGetValue(kind, out var provider)
        ? provider : throw new InvalidOperationException($"Provider '{kind}' is not configured.");
    private async Task<ProviderConnection> GetOwned(Guid id, Guid owner, bool tracking, CancellationToken token) =>
        await repository.GetAsync(id, owner, tracking, token) ?? throw new KeyNotFoundException("Connection not found.");
    private string? Unprotect(ProviderConnection entity) => entity.ProtectedCredential is null ? null
        : protector.UnprotectCredential(entity.ProtectedCredential, CredentialContext(entity, entity.CredentialVersion));
    private async Task<string?> PrepareCredentialAsync(
        ProviderConnection entity,
        IAiProvider provider,
        CancellationToken token)
    {
        var credential = Unprotect(entity);
        var refreshed = await provider.RefreshCredentialAsync(credential, token);
        if (!string.Equals(credential, refreshed, StringComparison.Ordinal) && refreshed is not null)
        {
            entity.ReplaceCredential(protector.ProtectCredential(refreshed,
                    CredentialContext(entity, entity.CredentialVersion + 1)),
                protector.Fingerprint(refreshed), timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(token);
        }
        return refreshed;
    }
    private static string? Normalize(AiProviderKind provider, string? credential)
    {
        if (provider is AiProviderKind.DeterministicTest or AiProviderKind.GitHubCopilotLocalCli)
            return null;
        var value = credential?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new ArgumentException("Valid credential required.");
        return value;
    }
    private static string NormalizeGitHubFineGrainedPat(string? credential)
    {
        const string fineGrainedPatPrefix = "github_pat_";
        var value = credential?.Trim() ?? string.Empty;
        if (!value.StartsWith(fineGrainedPatPrefix, StringComparison.Ordinal)
            || value.Length <= fineGrainedPatPrefix.Length
            || value.Length > 8192
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ProviderCredentialRejectedException("UnsupportedGitHubCredentialType",
                "Підтримується лише GitHub fine-grained PAT із префіксом github_pat_.");
        return value;
    }
    private static string NormalizeGitHubLogin(string? login)
    {
        var value = login?.Trim().TrimStart('@') ?? string.Empty;
        if (value.Length is < 1 or > ProviderConnection.ExternalAccountLoginMaxLength
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ProviderCredentialRejectedException("InvalidExpectedGitHubLogin",
                "Вкажіть очікуваний GitHub login перед перевіркою PAT.");
        return value;
    }
    private static ProviderCredentialRejectedException GitHubPatRejected(
        ProviderCredentialValidationKind kind) => kind switch
        {
            ProviderCredentialValidationKind.InvalidCredential
                or ProviderCredentialValidationKind.RequiresReconnect =>
                new(kind.ToString(), "GitHub PAT недійсний або відкликаний."),
            ProviderCredentialValidationKind.NotEntitled =>
                new(kind.ToString(), "Для підтвердженого GitHub-акаунта Copilot недоступний."),
            ProviderCredentialValidationKind.PolicyBlocked =>
                new(kind.ToString(), "GitHub Copilot заблоковано політикою акаунта або організації."),
            ProviderCredentialValidationKind.InsufficientPermissions =>
                new(kind.ToString(), "Fine-grained PAT не має дозволу Copilot Requests."),
            ProviderCredentialValidationKind.RateLimited =>
                new(kind.ToString(), "GitHub тимчасово обмежив кількість запитів; PAT не збережено."),
            ProviderCredentialValidationKind.TemporarilyUnavailable =>
                new(kind.ToString(), "GitHub Copilot тимчасово недоступний; PAT не збережено."),
            ProviderCredentialValidationKind.ModelCatalogUnavailable =>
                new("GitHubModelCatalogUnavailable",
                    "GitHub не повернув доступний каталог моделей; PAT не збережено."),
            _ => new(kind.ToString(), "Не вдалося безпечно перевірити GitHub PAT; PAT не збережено."),
        };
    private static ProviderCredentialRejectedException GitHubConnectionFlowRequired() =>
        new("GitHubConnectionFlowRequired",
            "GitHub Copilot потрібно підключати через OAuth, fine-grained PAT або окремий Local CLI flow.");
    private static ProviderConnectionDto Map(ProviderConnection x) => new(x.Id, x.Provider, x.DisplayName,
        x.Status, x.CredentialFingerprint, x.LastValidatedAt, x.LastSyncedAt, x.RevokedAt,
        x.CredentialVersion, x.QuotaSource, x.QuotaState, x.LastErrorCategory, x.LastSafeError,
        x.ExternalAccountId, x.ExternalAccountLogin, x.Version,
        null, "Unavailable", x.LastSuccessfulValidationAt, x.LastSyncedAt, null,
        AuthenticationStatus(x), ModelCatalogStatus(x),
        x.Provider == AiProviderKind.GitHubCopilotLocalCli
            ? "LocalCopilotCliRuntime" : "ProviderValidation");

    private static string AuthenticationStatus(ProviderConnection connection) => connection.Status switch
    {
        ProviderConnectionStatus.Active or ProviderConnectionStatus.QuotaExhausted => "Active",
        ProviderConnectionStatus.RequiresReconnect => "RequiresReconnect",
        ProviderConnectionStatus.Invalid or ProviderConnectionStatus.Revoked => "Unauthenticated",
        ProviderConnectionStatus.InsufficientPermissions => "InsufficientPermissions",
        ProviderConnectionStatus.NotEntitled => "SubscriptionUnavailable",
        ProviderConnectionStatus.PolicyBlocked => "PolicyBlocked",
        _ => "Unknown",
    };

    private static string ModelCatalogStatus(ProviderConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.AvailableModelsJson) && connection.LastSyncedAt is not null
            ? "Synced"
            : connection.Status == ProviderConnectionStatus.Active
                ? "Unavailable"
                : "Stale";
    private static void ApplyValidation(
        ProviderConnection entity,
        ProviderCredentialValidationResult validation,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(validation.ExternalAccountId)
            && !string.IsNullOrWhiteSpace(validation.ExternalAccountLogin))
            entity.BindExternalAccount(validation.ExternalAccountId, validation.ExternalAccountLogin, now);

        switch (validation.Kind)
        {
            case ProviderCredentialValidationKind.Valid:
                entity.MarkValidated(true, now);
                if (validation.Models is { Count: > 0 })
                    entity.SynchronizeModels(JsonSerializer.Serialize(validation.Models), now);
                entity.UpdateQuota(validation.QuotaSource, validation.QuotaState, now);
                break;
            case ProviderCredentialValidationKind.InvalidCredential:
                entity.MarkValidationFailed(
                    ProviderConnectionStatus.Invalid,
                    "Authentication",
                    "Credential недійсний або відкликаний.",
                    now);
                break;
            case ProviderCredentialValidationKind.RequiresReconnect:
                entity.MarkValidationFailed(ProviderConnectionStatus.RequiresReconnect, "Authentication",
                    "GitHub-токен недійсний або відкликаний. Підключіть GitHub повторно.", now);
                break;
            case ProviderCredentialValidationKind.NotEntitled:
                entity.MarkValidationFailed(ProviderConnectionStatus.NotEntitled, "Entitlement",
                    "GitHub підтвердив, що для цього акаунта Copilot недоступний.", now);
                break;
            case ProviderCredentialValidationKind.PolicyBlocked:
                entity.MarkValidationFailed(ProviderConnectionStatus.PolicyBlocked, "PolicyBlocked",
                    "Доступ до GitHub Copilot заблоковано політикою акаунта або організації.", now);
                break;
            case ProviderCredentialValidationKind.OAuthAppUnsupported:
                entity.MarkValidationFailed(ProviderConnectionStatus.OAuthAppUnsupported, "OAuthAppUnsupported",
                    "GitHub не дозволив Copilot-запити для цього OAuth App. Зверніться до GitHub Support із request ID із журналу.", now);
                break;
            case ProviderCredentialValidationKind.InsufficientPermissions:
                entity.MarkValidationFailed(ProviderConnectionStatus.InsufficientPermissions,
                    "InsufficientPermissions",
                    "GitHub не надав цьому OAuth credential доступ до Copilot API (403). " +
                    "Для уточнення причини потрібен request ID у GitHub Support або порівняння з official device flow.", now);
                break;
            case ProviderCredentialValidationKind.QuotaExhausted:
                if (validation.Models is { Count: > 0 })
                    entity.SynchronizeModels(JsonSerializer.Serialize(validation.Models), now);
                entity.UpdateQuota(validation.QuotaSource, validation.QuotaState, now);
                entity.MarkValidationFailed(ProviderConnectionStatus.QuotaExhausted, "QuotaExhausted",
                    "Квоту GitHub Copilot вичерпано. Підключення та каталог моделей залишаються дійсними.", now);
                break;
            case ProviderCredentialValidationKind.RateLimited:
                entity.MarkValidationFailed(ProviderConnectionStatus.Degraded, "RateLimited",
                    "GitHub Copilot тимчасово обмежив кількість запитів — повторіть пізніше.", now);
                break;
            case ProviderCredentialValidationKind.TemporarilyUnavailable:
                entity.MarkValidationFailed(ProviderConnectionStatus.Degraded, "TemporarilyUnavailable",
                    "GitHub Copilot тимчасово недоступний — повторіть пізніше.", now);
                break;
            case ProviderCredentialValidationKind.ModelCatalogUnavailable:
                entity.MarkValidationFailed(ProviderConnectionStatus.Degraded, "ModelCatalogUnavailable",
                    "GitHub Copilot не повернув доступний каталог моделей.", now);
                break;
            default:
                entity.MarkValidationFailed(ProviderConnectionStatus.Degraded, "Unknown",
                    "Не вдалося безпечно підтвердити підключення — повторіть пізніше.", now);
                break;
        }
    }
    private static ProviderCredentialProtectionContext CredentialContext(ProviderConnection entity, int version) =>
        new(entity.Provider, entity.OwnerUserId, entity.Id, version);
}

internal static class ToolCheckpointAuditSizer
{
    private const int MaximumToolOutputBytes = 8192;

    internal static bool Fits(
        AiExecutionRequest request,
        IReadOnlyCollection<ToolInvocationResult> completed,
        ToolCallRequest next,
        string? partialOutput,
        int availableBytes,
        int maximumCheckpointUtf8Bytes) => availableBytes is > 0
        && availableBytes <= maximumCheckpointUtf8Bytes
        && MeasureWorstCaseUtf8Bytes(request, completed, next, partialOutput) <= availableBytes;

    internal static int MeasureWorstCaseUtf8Bytes(
        AiExecutionRequest request,
        IReadOnlyCollection<ToolInvocationResult> completed,
        ToolCallRequest next,
        string? partialOutput)
    {
        var worstCase = new ToolInvocationResult(next.ToolName, true,
            new string('x', MaximumToolOutputBytes), null, next.CallId, next.ArgumentsJson,
            "Succeeded", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue,
            $"{request.IdempotencyKey}:tool:{next.CallId}");
        var baseline = JsonSerializer.SerializeToUtf8Bytes(new
        {
            partialOutput = (string?)null,
            completedToolCalls = Array.Empty<ToolInvocationResult>(),
        }).Length;
        var candidate = JsonSerializer.SerializeToUtf8Bytes(new
        {
            partialOutput,
            completedToolCalls = completed.Append(worstCase),
        }).Length;
        return candidate - baseline;
    }
}
