using AiControlCenter.ControlPlane.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

internal sealed class AgentDefinitionConfiguration : IEntityTypeConfiguration<AgentDefinition>
{
    public void Configure(EntityTypeBuilder<AgentDefinition> builder)
    {
        builder.ToTable("agent_definitions", table =>
        {
            table.HasCheckConstraint("ck_agent_definitions_status", "status IN ('Inactive', 'Active')");
            table.HasCheckConstraint("ck_agent_definitions_execution_type", "execution_type = 'Test'");
            table.HasCheckConstraint(
                "ck_agent_definitions_code_format",
                $"char_length(code) BETWEEN 2 AND {AgentDefinition.CodeMaxLength} "
                + "AND code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            table.HasCheckConstraint(
                "ck_agent_definitions_name_length",
                $"char_length(btrim(name)) BETWEEN 2 AND {AgentDefinition.NameMaxLength}");
            table.HasCheckConstraint(
                "ck_agent_definitions_timestamps",
                "updated_at >= created_at "
                + "AND (archived_at IS NULL OR archived_at BETWEEN created_at AND updated_at)");
        });
        builder.HasKey(agent => agent.Id);
        builder.Property(agent => agent.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(agent => agent.DirectionId).HasColumnName("direction_id");
        builder.HasOne<Direction>().WithMany().HasForeignKey(agent => agent.DirectionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_agent_definitions_directions_direction_id");
        builder.Property(agent => agent.Name).HasColumnName("name")
            .HasMaxLength(AgentDefinition.NameMaxLength);
        builder.Property(agent => agent.Code).HasColumnName("code")
            .HasMaxLength(AgentDefinition.CodeMaxLength);
        builder.HasIndex(agent => agent.Code).IsUnique().HasDatabaseName("ux_agent_definitions_code");
        builder.Property(agent => agent.Description).HasColumnName("description")
            .HasMaxLength(AgentDefinition.DescriptionMaxLength);
        builder.Property(agent => agent.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(16);
        builder.Property(agent => agent.ExecutionType).HasColumnName("execution_type")
            .HasConversion<string>().HasMaxLength(16);
        builder.Property(agent => agent.CreatedAt).HasColumnName("created_at");
        builder.Property(agent => agent.UpdatedAt).HasColumnName("updated_at");
        builder.Property(agent => agent.ArchivedAt).HasColumnName("archived_at");
        builder.Property(agent => agent.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasIndex(agent => new { agent.ArchivedAt, agent.Status, agent.DirectionId, agent.Name, agent.Id })
            .HasDatabaseName("ix_agent_definitions_list");
    }
}
