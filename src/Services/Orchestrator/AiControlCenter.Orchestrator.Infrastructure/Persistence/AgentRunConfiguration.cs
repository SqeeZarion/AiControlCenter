using AiControlCenter.Orchestrator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence;

//Цей файл пояснює EF Core, як класи AgentRun і RunStep мають зберігатися в PostgreSQL.
internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable("agent_runs", table =>
        {
            table.HasCheckConstraint("ck_agent_runs_status", "status IN ('Queued','Running','Succeeded','Failed','PausedProviderUnavailable','PausedInsufficientFunds','Cancelled')");
            table.HasCheckConstraint("ck_agent_runs_execution_type", "execution_type IN ('Test','Ai')");
            table.HasCheckConstraint("ck_agent_runs_expected_outcome", "expected_outcome IN ('Succeed','Fail')");
            table.HasCheckConstraint("ck_agent_runs_revision", "revision >= 1");
            table.HasCheckConstraint(
                "ck_agent_runs_snapshot_versions",
                "agent_version > 0 AND direction_version > 0");
            table.HasCheckConstraint(
                "ck_agent_runs_timestamps",
                "(started_at IS NULL OR started_at >= created_at) AND "
                + "(completed_at IS NULL OR completed_at >= COALESCE(started_at, created_at))");
            table.HasCheckConstraint(
                "ck_agent_runs_lifecycle",
                "(status = 'Queued' AND started_at IS NULL AND completed_at IS NULL) OR "
                + "(status = 'Running' AND started_at IS NOT NULL AND completed_at IS NULL) OR "
                + "(status = 'Succeeded' AND started_at IS NOT NULL AND completed_at IS NOT NULL) OR "
                + "(status = 'Failed' AND completed_at IS NOT NULL) OR "
                + "(status IN ('PausedProviderUnavailable','PausedInsufficientFunds') AND started_at IS NOT NULL AND completed_at IS NULL) OR "
                + "(status = 'Cancelled' AND completed_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_agent_runs_terminal_payload",
                "(status IN ('Queued','Running') AND result IS NULL AND error IS NULL) OR "
                + "(status IN ('PausedProviderUnavailable','PausedInsufficientFunds') AND result IS NULL AND error IS NOT NULL) OR "
                + "(status = 'Succeeded' AND error IS NULL) OR "
                + "(status IN ('Failed','Cancelled') AND error IS NOT NULL AND btrim(error) <> '')");
            table.HasCheckConstraint("ck_agent_runs_checkpoint",
                "(checkpoint_json IS NULL AND checkpoint_version = 0 AND checkpoint_hash IS NULL AND checkpoint_updated_at IS NULL) OR "
                + "(checkpoint_json IS NOT NULL AND checkpoint_version > 0 AND length(checkpoint_hash) = 64 AND checkpoint_updated_at IS NOT NULL)");
        });
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(run => run.AgentId).HasColumnName("agent_id");
        builder.Property(run => run.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(run => run.CorrelationId).HasColumnName("correlation_id")
            .HasMaxLength(AgentRun.CorrelationIdMaxLength);
        builder.Property(run => run.AgentName).HasColumnName("agent_name").HasMaxLength(AgentRun.NameMaxLength);
        builder.Property(run => run.AgentCode).HasColumnName("agent_code").HasMaxLength(AgentRun.CodeMaxLength);
        builder.Property(run => run.AgentDescription).HasColumnName("agent_description")
            .HasMaxLength(AgentRun.DescriptionMaxLength);
        builder.Property(run => run.AgentVersion).HasColumnName("agent_version");
        builder.Property(run => run.DirectionId).HasColumnName("direction_id");
        builder.Property(run => run.DirectionName).HasColumnName("direction_name").HasMaxLength(AgentRun.NameMaxLength);
        builder.Property(run => run.DirectionCode).HasColumnName("direction_code").HasMaxLength(AgentRun.CodeMaxLength);
        builder.Property(run => run.DirectionVersion).HasColumnName("direction_version");
        builder.Property(run => run.ExecutionType).HasColumnName("execution_type").HasMaxLength(16);
        builder.Property(run => run.SystemPrompt).HasColumnName("system_prompt").HasMaxLength(16000);
        builder.Property(run => run.ModelRoutesJson).HasColumnName("model_routes_json").HasColumnType("jsonb");
        builder.Property(run => run.ToolPolicyJson).HasColumnName("tool_policy_json").HasColumnType("jsonb");
        builder.Property(run => run.MaxInputTokens).HasColumnName("max_input_tokens");
        builder.Property(run => run.MaxOutputTokens).HasColumnName("max_output_tokens");
        builder.Property(run => run.MaxEstimatedCostUsd).HasColumnName("max_estimated_cost_usd").HasPrecision(18, 8);
        builder.Property(run => run.MaxAttempts).HasColumnName("max_attempts");
        builder.Property(run => run.Input).HasColumnName("input").HasMaxLength(AgentRun.InputMaxLength);
        builder.Property(run => run.ExpectedOutcome).HasColumnName("expected_outcome")
            .HasConversion<string>().HasMaxLength(16);
        builder.Property(run => run.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(run => run.ExecutionMessageId).HasColumnName("execution_message_id");
        builder.Property(run => run.Revision).HasColumnName("revision");
        builder.Property(run => run.CreatedAt).HasColumnName("created_at");
        builder.Property(run => run.StartedAt).HasColumnName("started_at");
        builder.Property(run => run.CompletedAt).HasColumnName("completed_at");
        builder.Property(run => run.Result).HasColumnName("result").HasMaxLength(AgentRun.ResultMaxLength);
        builder.Property(run => run.Error).HasColumnName("error").HasMaxLength(AgentRun.ErrorMaxLength);
        builder.Property(run => run.CheckpointJson).HasColumnName("checkpoint_json").HasColumnType("jsonb");
        builder.Property(run => run.CheckpointVersion).HasColumnName("checkpoint_version");
        builder.Property(run => run.CheckpointHash).HasColumnName("checkpoint_hash").HasMaxLength(64);
        builder.Property(run => run.CheckpointUpdatedAt).HasColumnName("checkpoint_updated_at");
        builder.Property(run => run.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasMany(run => run.Steps).WithOne().HasForeignKey(step => step.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_run_steps_agent_runs_run_id");
        builder.Navigation(run => run.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(run => run.Attempts).WithOne().HasForeignKey(attempt => attempt.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_ai_provider_attempts_agent_runs_run_id");
        builder.Navigation(run => run.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(run => new { run.OwnerUserId, run.CreatedAt, run.Id })
            .HasDatabaseName("ix_agent_runs_owner_created");
        builder.HasIndex(run => new { run.Status, run.CreatedAt, run.Id })
            .HasDatabaseName("ix_agent_runs_status_created");
        builder.HasIndex(run => run.ExecutionMessageId).IsUnique()
            .HasFilter("execution_message_id IS NOT NULL")
            .HasDatabaseName("ux_agent_runs_execution_message_id");
    }
}

internal sealed class AiProviderAttemptConfiguration : IEntityTypeConfiguration<AiProviderAttempt>
{
    public void Configure(EntityTypeBuilder<AiProviderAttempt> builder)
    {
        builder.ToTable("ai_provider_attempts", table =>
        {
            table.HasCheckConstraint("ck_ai_provider_attempts_sequence", "sequence > 0");
            table.HasCheckConstraint("ck_ai_provider_attempts_tokens", "input_tokens >= 0 AND output_tokens >= 0");
            table.HasCheckConstraint("ck_ai_provider_attempts_cost", "estimated_cost_usd IS NULL OR estimated_cost_usd >= 0");
            table.HasCheckConstraint("ck_ai_provider_attempts_route", "route_priority > 0 AND latency_milliseconds >= 0 AND request_count = 1");
            table.HasCheckConstraint("ck_ai_provider_attempts_totals", "cached_input_tokens >= 0 AND total_tokens = input_tokens + output_tokens");
            table.HasCheckConstraint("ck_ai_provider_attempts_timestamps", "completed_at >= started_at");
            table.HasCheckConstraint("ck_ai_provider_attempts_generation_dispatched",
                "generation_dispatched IS NULL OR generation_dispatched IN ('true','false','unknown')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.AgentRunId).HasColumnName("agent_run_id");
        builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(32);
        builder.Property(x => x.Model).HasColumnName("model").HasMaxLength(128);
        builder.Property(x => x.ActualModel).HasColumnName("actual_model").HasMaxLength(128);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
        builder.Property(x => x.FailureKind).HasColumnName("failure_kind").HasMaxLength(64);
        builder.Property(x => x.ProviderRequestId).HasColumnName("provider_request_id").HasMaxLength(256);
        builder.Property(x => x.InputTokens).HasColumnName("input_tokens");
        builder.Property(x => x.CachedInputTokens).HasColumnName("cached_input_tokens");
        builder.Property(x => x.OutputTokens).HasColumnName("output_tokens");
        builder.Property(x => x.TotalTokens).HasColumnName("total_tokens");
        builder.Property(x => x.EstimatedCostUsd).HasColumnName("estimated_cost_usd").HasPrecision(18, 8);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(160);
        builder.Property(x => x.RoutePriority).HasColumnName("route_priority");
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(AgentRun.CorrelationIdMaxLength);
        builder.Property(x => x.LatencyMilliseconds).HasColumnName("latency_milliseconds");
        builder.Property(x => x.RequestCount).HasColumnName("request_count");
        builder.Property(x => x.UsageSource).HasColumnName("usage_source").HasMaxLength(24);
        builder.Property(x => x.CostSource).HasColumnName("cost_source").HasMaxLength(24);
        builder.Property(x => x.FinishStatus).HasColumnName("finish_status").HasMaxLength(32);
        builder.Property(x => x.FallbackReason).HasColumnName("fallback_reason").HasMaxLength(64);
        builder.Property(x => x.PartialOutput).HasColumnName("partial_output").HasMaxLength(AgentRun.ResultMaxLength);
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(128);
        builder.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(64);
        builder.Property(x => x.GenerationDispatched).HasColumnName("generation_dispatched").HasMaxLength(16);
        builder.Property(x => x.SafeExceptionType).HasColumnName("safe_exception_type").HasMaxLength(128);
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.HasIndex(x => new { x.AgentRunId, x.Sequence }).IsUnique().HasDatabaseName("ux_ai_provider_attempts_run_sequence");
        builder.HasIndex(x => new { x.OwnerUserId, x.IdempotencyKey }).IsUnique()
            .HasDatabaseName("ux_ai_provider_attempts_owner_idempotency");
    }
}

internal sealed class RunStepConfiguration : IEntityTypeConfiguration<RunStep>
{
    public void Configure(EntityTypeBuilder<RunStep> builder)
    {
        builder.ToTable("run_steps", table =>
        {
            table.HasCheckConstraint("ck_run_steps_sequence", $"sequence BETWEEN 1 AND {AgentRun.MaximumSteps}");
            table.HasCheckConstraint("ck_run_steps_status", "status IN ('Running','Succeeded','Failed','Paused')");
            table.HasCheckConstraint(
                "ck_run_steps_timestamps",
                "updated_at >= started_at AND (completed_at IS NULL OR completed_at >= started_at)");
            table.HasCheckConstraint(
                "ck_run_steps_lifecycle",
                "(status IN ('Running','Paused') AND completed_at IS NULL) OR "
                + "(status IN ('Succeeded','Failed') AND completed_at IS NOT NULL)");
        });
        builder.HasKey(step => step.Id);
        builder.Property(step => step.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(step => step.AgentRunId).HasColumnName("agent_run_id");
        builder.Property(step => step.Sequence).HasColumnName("sequence");
        builder.Property(step => step.Name).HasColumnName("name").HasMaxLength(RunStep.NameMaxLength);
        builder.Property(step => step.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(step => step.Log).HasColumnName("log").HasMaxLength(RunStep.LogMaxLength);
        builder.Property(step => step.StartedAt).HasColumnName("started_at");
        builder.Property(step => step.UpdatedAt).HasColumnName("updated_at");
        builder.Property(step => step.CompletedAt).HasColumnName("completed_at");
        builder.HasIndex(step => new { step.AgentRunId, step.Sequence }).IsUnique()
            .HasDatabaseName("ux_run_steps_run_sequence");
    }
}
