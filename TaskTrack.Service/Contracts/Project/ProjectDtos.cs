using System.ComponentModel.DataAnnotations;

namespace TaskTrack.Service.Contracts;

public abstract class ProjectWriteRequest : IValidatableObject
{
    [Required, StringLength(200)] public string ProjectName { get; set; } = string.Empty;
    public string? Description { get; set; }
    [Required] public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    [Range(0, 3)] public ProjectStatus Status { get; set; }
    [Range(1, int.MaxValue)] public int DepartmentId { get; set; }
    public bool IsActive { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (StartDate.HasValue && EndDate < StartDate)
            yield return new ValidationResult("EndDate must be on or after StartDate.", [nameof(EndDate)]);
    }
}
public sealed class ProjectCreateRequest : ProjectWriteRequest { }
public sealed class ProjectUpdateRequest : ProjectWriteRequest { }
public sealed class ProjectSearchRequest
{
    public string? Name { get; set; }
    [Range(0, 3)] public ProjectStatus? Status { get; set; }
    [Range(1, int.MaxValue)] public int? DepartmentId { get; set; }
}
public record ProjectListItem
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public ProjectStatus Status { get; init; }
    public int DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedDate { get; init; }
}
public sealed record ProjectDetail : ProjectListItem
{
    public IReadOnlyList<TaskListItem> Tasks { get; init; } = [];
}
