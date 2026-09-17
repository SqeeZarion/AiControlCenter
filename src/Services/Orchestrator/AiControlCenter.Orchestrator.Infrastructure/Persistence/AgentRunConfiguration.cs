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
            table.HasCheckConstraint("ck_agent_runs_status", "status IN ('Queued','Running','Succeeded','Failed')");
            table.HasCheckConstraint("ck_agent_runs_execution_type", "execution_type = 'Test'");
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
                + "(status = 'Failed' AND completed_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_agent_runs_terminal_payload",
                "(status IN ('Queued','Running') AND result IS NULL AND error IS NULL) OR "
                + "(status = 'Succeeded' AND error IS NULL) OR "
                + "(status = 'Failed' AND error IS NOT NULL AND btrim(error) <> '')");
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
        builder.Property(run => run.Input).HasColumnName("input").HasMaxLength(AgentRun.InputMaxLength);
        builder.Property(run => run.ExpectedOutcome).HasColumnName("expected_outcome")
            .HasConversion<string>().HasMaxLength(16);
        builder.Property(run => run.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(run => run.ExecutionMessageId).HasColumnName("execution_message_id");
        builder.Property(run => run.Revision).HasColumnName("revision");
        builder.Property(run => run.CreatedAt).HasColumnName("created_at");
        builder.Property(run => run.StartedAt).HasColumnName("started_at");
        builder.Property(run => run.CompletedAt).HasColumnName("completed_at");
        builder.Property(run => run.Result).HasColumnName("result").HasMaxLength(AgentRun.ResultMaxLength);
        builder.Property(run => run.Error).HasColumnName("error").HasMaxLength(AgentRun.ErrorMaxLength);
        builder.Property(run => run.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasMany(run => run.Steps).WithOne().HasForeignKey(step => step.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_run_steps_agent_runs_run_id");
        builder.Navigation(run => run.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(run => new { run.OwnerUserId, run.CreatedAt, run.Id })
            .HasDatabaseName("ix_agent_runs_owner_created");
        builder.HasIndex(run => new { run.Status, run.CreatedAt, run.Id })
            .HasDatabaseName("ix_agent_runs_status_created");
        builder.HasIndex(run => run.ExecutionMessageId).IsUnique()
            .HasFilter("execution_message_id IS NOT NULL")
            .HasDatabaseName("ux_agent_runs_execution_message_id");
    }
}

internal sealed class RunStepConfiguration : IEntityTypeConfiguration<RunStep>
{
    public void Configure(EntityTypeBuilder<RunStep> builder)
    {
        builder.ToTable("run_steps", table =>
        {
            table.HasCheckConstraint("ck_run_steps_sequence", $"sequence BETWEEN 1 AND {AgentRun.MaximumSteps}");
            table.HasCheckConstraint("ck_run_steps_status", "status IN ('Running','Succeeded','Failed')");
            table.HasCheckConstraint(
                "ck_run_steps_timestamps",
                "updated_at >= started_at AND (completed_at IS NULL OR completed_at >= started_at)");
            table.HasCheckConstraint(
                "ck_run_steps_lifecycle",
                "(status = 'Running' AND completed_at IS NULL) OR "
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
