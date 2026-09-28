using System.ComponentModel.DataAnnotations;

namespace TaskTrack.Service.Contracts;

public abstract class TaskWriteRequest : IValidatableObject
{
    [Required, StringLength(300)] public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    [Range(0, 3)] public TaskStatus Status { get; set; }
    [Range(0, 3)] public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public DateOnly? DueDate { get; set; }
    [Range(1, int.MaxValue)] public int ProjectId { get; set; }
    public bool IsActive { get; set; } = true;
    private int[] tagIds = [];
    public int[]? TagIds { get => tagIds.ToArray(); set => tagIds = value?.Distinct().ToArray() ?? []; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (tagIds.Any(id => id <= 0))
            yield return new ValidationResult("Tag IDs must be positive.", [nameof(TagIds)]);
    }
}
public sealed class TaskCreateRequest : TaskWriteRequest { }
public sealed class TaskUpdateRequest : TaskWriteRequest { }
public sealed class TaskSearchRequest
{
    public string? Title { get; set; }
    [Range(0, 3)] public TaskStatus? Status { get; set; }
    [Range(0, 3)] public TaskPriority? Priority { get; set; }
    [Range(1, int.MaxValue)] public int? ProjectId { get; set; }
    [Range(1, int.MaxValue)] public int? TagId { get; set; }
}
public record TaskListItem
{
    public int TaskId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public TaskStatus Status { get; init; }
    public TaskPriority Priority { get; init; }
    public DateOnly? DueDate { get; init; }
    public int ProjectId { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedDate { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public IReadOnlyList<TagListItem> Tags { get; init; } = [];
}
public sealed record TaskDetail : TaskListItem;
