using AiControlCenter.ControlPlane.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

internal sealed class DirectionConfiguration : IEntityTypeConfiguration<Direction>
{
    public void Configure(EntityTypeBuilder<Direction> builder)
    {
        builder.ToTable("directions", table =>
        {
            table.HasCheckConstraint(
                "ck_directions_status",
                "status IN ('Inactive', 'Active')");
            table.HasCheckConstraint(
                "ck_directions_sort_order",
                $"sort_order >= 0 AND sort_order <= {Direction.SortOrderMax}");
            table.HasCheckConstraint(
                "ck_directions_code_format",
                $"char_length(code) BETWEEN 2 AND {Direction.CodeMaxLength} "
                + "AND code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            table.HasCheckConstraint(
                "ck_directions_name_length",
                $"char_length(btrim(name)) BETWEEN 2 AND {Direction.NameMaxLength}");
            table.HasCheckConstraint(
                "ck_directions_timestamps",
                "updated_at >= created_at "
                + "AND (archived_at IS NULL OR archived_at BETWEEN created_at AND updated_at)");
        });
        builder.HasKey(direction => direction.Id);
        builder.Property(direction => direction.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(direction => direction.Name)
            .HasColumnName("name")
            .HasMaxLength(Direction.NameMaxLength);
        builder.Property(direction => direction.Code)
            .HasColumnName("code")
            .HasMaxLength(Direction.CodeMaxLength);
        builder.HasIndex(direction => direction.Code)
            .IsUnique()
            .HasDatabaseName("ux_directions_code");
        builder.Property(direction => direction.Description)
            .HasColumnName("description")
            .HasMaxLength(Direction.DescriptionMaxLength);
        builder.Property(direction => direction.Icon)
            .HasColumnName("icon")
            .HasMaxLength(Direction.IconMaxLength);
        builder.Property(direction => direction.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(direction => direction.SortOrder).HasColumnName("sort_order");
        builder.Property(direction => direction.CreatedAt).HasColumnName("created_at");
        builder.Property(direction => direction.UpdatedAt).HasColumnName("updated_at");
        builder.Property(direction => direction.ArchivedAt).HasColumnName("archived_at");
        builder.Property(direction => direction.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasIndex(direction => new
            {
                direction.ArchivedAt,
                direction.Status,
                direction.SortOrder,
                direction.Name,
                direction.Id,
            })
            .HasDatabaseName("ix_directions_list");
    }
}
