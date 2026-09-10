using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.UnitTests;

public sealed class DirectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesValuesAndSetsTimestamps()
    {
        var direction = Direction.Create(
            Guid.NewGuid(),
            "  AI   Notes  ",
            " AI_Notes ",
            "  Analyse ideas  ",
            "  spark  ",
            DirectionStatus.Active,
            10,
            Now);

        Assert.Equal("AI Notes", direction.Name);
        Assert.Equal("ai-notes", direction.Code);
        Assert.Equal("Analyse ideas", direction.Description);
        Assert.Equal("spark", direction.Icon);
        Assert.Equal(Now, direction.CreatedAt);
        Assert.Equal(Now, direction.UpdatedAt);
        Assert.False(direction.IsArchived);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public void CreateRejectsInvalidName(string name) => Assert.Throws<ArgumentException>(() =>
        Direction.Create(Guid.NewGuid(), name, "valid-code", null, null, DirectionStatus.Active, 0, Now));

    [Theory]
    [InlineData("invalid/code")]
    [InlineData("x")]
    public void CreateRejectsInvalidCode(string code) => Assert.Throws<ArgumentException>(() =>
        Direction.Create(Guid.NewGuid(), "Valid", code, null, null, DirectionStatus.Active, 0, Now));

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void CreateRejectsInvalidSortOrder(int sortOrder) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        Direction.Create(Guid.NewGuid(), "Valid", "valid", null, null, DirectionStatus.Active, sortOrder, Now));

    [Fact]
    public void StatusAndSortOrderChangesUpdateTimestamp()
    {
        var direction = Create();
        direction.ChangeStatus(DirectionStatus.Inactive, Now.AddMinutes(1));
        direction.ChangeSortOrder(42, Now.AddMinutes(2));
        Assert.Equal(DirectionStatus.Inactive, direction.Status);
        Assert.Equal(42, direction.SortOrder);
        Assert.Equal(Now.AddMinutes(2), direction.UpdatedAt);
    }

    [Fact]
    public void ArchiveAndRestoreAreIdempotentAndArchivedDirectionCannotBeEdited()
    {
        var direction = Create();
        Assert.True(direction.Archive(Now.AddMinutes(1)));
        Assert.False(direction.Archive(Now.AddMinutes(2)));
        Assert.Throws<DirectionRuleViolationException>(() =>
            direction.Update(
                "Changed",
                "changed",
                null,
                null,
                DirectionStatus.Inactive,
                2,
                Now.AddMinutes(3)));
        Assert.Throws<DirectionRuleViolationException>(() =>
            direction.ChangeStatus(DirectionStatus.Inactive, Now.AddMinutes(3)));
        Assert.Throws<DirectionRuleViolationException>(() =>
            direction.ChangeSortOrder(2, Now.AddMinutes(3)));
        Assert.True(direction.Restore(Now.AddMinutes(4)));
        Assert.False(direction.Restore(Now.AddMinutes(5)));
        direction.Update(
            "Changed",
            "changed",
            null,
            null,
            DirectionStatus.Inactive,
            2,
            Now.AddMinutes(6));
        Assert.Equal("Changed", direction.Name);
        Assert.Equal(DirectionStatus.Inactive, direction.Status);
        Assert.Equal(2, direction.SortOrder);
    }

    [Fact]
    public void ApplicationValidatorsMatchDomainBoundary()
    {
        var create = new CreateDirectionCommandValidator().Validate(new CreateDirectionCommand(
            "x", "bad/code", new string('x', 1001), null, (DirectionStatus)99, -1));
        var update = new UpdateDirectionCommandValidator().Validate(new UpdateDirectionCommand(
            "Valid", "valid", null, null, DirectionStatus.Active, 0, 0));
        Assert.False(create.IsValid);
        Assert.False(update.IsValid);
        Assert.Contains(create.Errors, error => error.PropertyName == nameof(CreateDirectionCommand.Code));
        Assert.Contains(update.Errors, error => error.PropertyName == nameof(UpdateDirectionCommand.Version));
    }

    [Fact]
    public void ApplicationValidatorsApplyTheSameNormalizationBoundaryAsDomain()
    {
        var createValidator = new CreateDirectionCommandValidator();

        var normalized = createValidator.Validate(new CreateDirectionCommand(
            "  AI   Notes  ",
            " AI___Notes ",
            "   ",
            "  spark  ",
            DirectionStatus.Active,
            0));
        var tooShortAfterNormalization = createValidator.Validate(new CreateDirectionCommand(
            "  x  ",
            "valid-code",
            null,
            null,
            DirectionStatus.Active,
            0));

        Assert.True(normalized.IsValid);
        Assert.Contains(
            tooShortAfterNormalization.Errors,
            error => error.PropertyName == nameof(CreateDirectionCommand.Name));
    }

    [Fact]
    public void NameLengthUsesUnicodeScalarValuesAfterNormalization()
    {
        Assert.Throws<ArgumentException>(() => Direction.Create(
            Guid.NewGuid(), "😀", "single-emoji", null, null, DirectionStatus.Active, 0, Now));

        var boundaryName = string.Concat(Enumerable.Repeat("😀", Direction.NameMaxLength));
        var direction = Direction.Create(
            Guid.NewGuid(), boundaryName, "emoji-boundary", null, null, DirectionStatus.Active, 0, Now);
        Assert.Equal(boundaryName, direction.Name);

        Assert.Throws<ArgumentException>(() => Direction.Create(
            Guid.NewGuid(),
            boundaryName + "😀",
            "emoji-over-boundary",
            null,
            null,
            DirectionStatus.Active,
            0,
            Now));

        var combiningCharacters = Direction.Create(
            Guid.NewGuid(), "e\u0301", "combining", null, null, DirectionStatus.Active, 0, Now);
        Assert.Equal("e\u0301", combiningCharacters.Name);
    }

    [Fact]
    public void NameNormalizationUsesUnicodeWhiteSpaceProperty()
    {
        Assert.Throws<ArgumentException>(() => Direction.Create(
            Guid.NewGuid(), "\u0085A", "nel-prefix", null, null, DirectionStatus.Active, 0, Now));

        foreach (var whiteSpace in new[] { '\u0085', '\u00A0', '\u2007', '\u202F' })
        {
            var direction = Direction.Create(
                Guid.NewGuid(),
                $"A{whiteSpace}{whiteSpace}B",
                $"space-{(int)whiteSpace:x4}",
                null,
                null,
                DirectionStatus.Active,
                0,
                Now);
            Assert.Equal("A B", direction.Name);
        }

        var byteOrderMark = Direction.Create(
            Guid.NewGuid(), "\uFEFFA", "byte-order-mark", null, null, DirectionStatus.Active, 0, Now);
        Assert.Equal("\uFEFFA", byteOrderMark.Name);
    }

    [Fact]
    public void AtomicUpdateValidatesEveryValueBeforeChangingTheAggregate()
    {
        var direction = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => direction.Update(
            "Changed",
            "changed",
            "Changed description",
            "changed-icon",
            (DirectionStatus)99,
            42,
            Now.AddMinutes(1)));

        Assert.Equal("Direction", direction.Name);
        Assert.Equal("direction", direction.Code);
        Assert.Null(direction.Description);
        Assert.Null(direction.Icon);
        Assert.Equal(DirectionStatus.Active, direction.Status);
        Assert.Equal(1, direction.SortOrder);
        Assert.Equal(Now, direction.UpdatedAt);
    }

    private static Direction Create() => Direction.Create(
        Guid.NewGuid(), "Direction", "direction", null, null, DirectionStatus.Active, 1, Now);
}
