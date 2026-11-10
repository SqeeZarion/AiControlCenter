using System.Data;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

internal sealed class ProviderBudgetManager(IntegrationsDbContext db) : IProviderBudgetManager
{
    public async Task<BudgetReservationResult> ReserveAsync(
        AiExecutionRequest request,
        AiProviderKind provider,
        string modelId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rates = ProviderPricingCatalog.Rates(provider, modelId);
        if (rates is null || request.MaxInputTokens is null or <= 0 || request.MaxOutputTokens is null or <= 0)
            return new(false, null, "paid_model_price_unknown");
        var perTurnReservation = ProviderPricingCatalog.Estimate(
            provider, modelId, request.MaxInputTokens.Value, request.MaxOutputTokens.Value);
        if (perTurnReservation is null || perTurnReservation <= 0)
            return new(false, null, "paid_model_price_unknown");
        var reservation = perTurnReservation * (1 + Math.Clamp(request.MaxToolCycles, 0, 4));
        if (request.RunCostCapUsd is not null && reservation > request.RunCostCapUsd
            || request.RouteCostCapUsd is not null && reservation > request.RouteCostCapUsd)
            return new(false, null, "local_budget_exceeded");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReserveInSerializableTransactionAsync(
                    request, provider, now, reservation.Value, cancellationToken);
            }
            catch (Exception exception) when (attempt < 2 && IsSerializationFailure(exception))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<BudgetReservationResult> ReserveInSerializableTransactionAsync(
        AiExecutionRequest request,
        AiProviderKind provider,
        DateTimeOffset now,
        decimal reservation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({request.OwnerUserId.ToString("N") + ":" + request.ConnectionId.ToString("N")}, 0))",
            cancellationToken);
        var duplicate = await db.ProviderExecutionReceipts.SingleOrDefaultAsync(
            item => item.OwnerUserId == request.OwnerUserId && item.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (duplicate is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, duplicate, "duplicate_or_pending_execution");
        }
        var since = now.AddDays(-31);
        var usage = await db.ProviderExecutionReceipts.AsNoTracking()
            .Where(item => item.OwnerUserId == request.OwnerUserId
                && item.ConnectionId == request.ConnectionId
                && item.Provider == provider
                && item.CreatedAt >= since)
            .ToArrayAsync(cancellationToken);
        static decimal Cost(ProviderExecutionReceipt item) => item.Status switch
        {
            ProviderExecutionReceiptStatus.Pending or ProviderExecutionReceiptStatus.Ambiguous => item.ReservedCostUsd,
            ProviderExecutionReceiptStatus.Completed => item.ActualCostUsd ?? item.ReservedCostUsd,
            _ => 0,
        };
        static int Requests(ProviderExecutionReceipt item) => item.Status switch
        {
            ProviderExecutionReceiptStatus.Pending or ProviderExecutionReceiptStatus.Ambiguous =>
                item.ReservedRequestCount,
            ProviderExecutionReceiptStatus.Completed or ProviderExecutionReceiptStatus.Released =>
                item.ActualRequestCount,
            _ => 0,
        };
        var daily = usage.Where(item => item.CreatedAt >= now.AddDays(-1)).ToArray();
        var monthly = usage.Where(item => item.CreatedAt >= now.AddDays(-30)).ToArray();
        var allowed = (request.MaxRequestsPerDay is null
                || daily.Sum(Requests) + 1 <= request.MaxRequestsPerDay)
            && (request.MaxRequestsPerMonth is null
                || monthly.Sum(Requests) + 1 <= request.MaxRequestsPerMonth)
            && (request.DailyCostCapUsd is null || daily.Sum(Cost) + reservation <= request.DailyCostCapUsd)
            && (request.MonthlyCostCapUsd is null || monthly.Sum(Cost) + reservation <= request.MonthlyCostCapUsd);
        if (!allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, null, "local_budget_exceeded");
        }
        var receipt = ProviderExecutionReceipt.Start(request.OwnerUserId, request.ConnectionId,
            request.IdempotencyKey, now, provider, reservation);
        db.ProviderExecutionReceipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, receipt, null);
    }

    public async Task<BudgetReservationResult> ReserveAdditionalRequestAsync(
        ProviderExecutionReceipt receipt,
        AiExecutionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReserveAdditionalInSerializableTransactionAsync(
                    receipt.Id, request, now, cancellationToken);
            }
            catch (Exception exception) when (attempt < 2 && IsSerializationFailure(exception))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<BudgetReservationResult> ReserveAdditionalInSerializableTransactionAsync(
        Guid receiptId,
        AiExecutionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({request.OwnerUserId.ToString("N") + ":" + request.ConnectionId.ToString("N")}, 0))",
            cancellationToken);
        var current = await db.ProviderExecutionReceipts.SingleAsync(
            item => item.Id == receiptId
                && item.OwnerUserId == request.OwnerUserId
                && item.ConnectionId == request.ConnectionId,
            cancellationToken);
        if (current.Status != ProviderExecutionReceiptStatus.Pending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, current, "execution_not_pending");
        }
        var usage = await db.ProviderExecutionReceipts.AsNoTracking()
            .Where(item => item.OwnerUserId == request.OwnerUserId
                && item.ConnectionId == request.ConnectionId
                && item.Provider == current.Provider
                && item.CreatedAt >= now.AddDays(-31))
            .ToArrayAsync(cancellationToken);
        static int Requests(ProviderExecutionReceipt item) => item.Status switch
        {
            ProviderExecutionReceiptStatus.Pending or ProviderExecutionReceiptStatus.Ambiguous =>
                item.ReservedRequestCount,
            ProviderExecutionReceiptStatus.Completed or ProviderExecutionReceiptStatus.Released =>
                item.ActualRequestCount,
            _ => 0,
        };
        var dailyRequests = usage.Where(item => item.CreatedAt >= now.AddDays(-1)).Sum(Requests);
        var monthlyRequests = usage.Where(item => item.CreatedAt >= now.AddDays(-30)).Sum(Requests);
        if (request.MaxRequestsPerDay is not null && dailyRequests + 1 > request.MaxRequestsPerDay
            || request.MaxRequestsPerMonth is not null && monthlyRequests + 1 > request.MaxRequestsPerMonth)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, current, "local_budget_exceeded");
        }
        current.ReserveAdditionalRequests(1);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, current, null);
    }

    private static bool IsSerializationFailure(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.SerializationFailure,
        DbUpdateException { InnerException: PostgresException postgres } =>
            postgres.SqlState == PostgresErrorCodes.SerializationFailure,
        InvalidOperationException { InnerException: DbUpdateException { InnerException: PostgresException postgres } } =>
            postgres.SqlState == PostgresErrorCodes.SerializationFailure,
        _ => false,
    };

    public async Task ReconcileAsync(
        ProviderExecutionReceipt receipt,
        AiExecutionResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        if (result.FailureKind == ProviderFailureKind.AmbiguousTimeout)
            receipt.MarkAmbiguous(json, now, result.OutboundRequestCount);
        else if (!result.Succeeded)
            receipt.Release(json, now, result.OutboundRequestCount);
        else
            receipt.Complete(json, now, result.EstimatedCostUsd, result.OutboundRequestCount);
        await db.SaveChangesAsync(cancellationToken);
    }
}
