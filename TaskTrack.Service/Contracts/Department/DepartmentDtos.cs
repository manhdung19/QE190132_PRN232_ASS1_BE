using System.ComponentModel.DataAnnotations;

namespace TaskTrack.Service.Contracts;

public abstract class DepartmentWriteRequest
{
    [Required, StringLength(100)] public string DepartmentName { get; set; } = string.Empty;
    [Required, StringLength(300)] public string DepartmentDescription { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
public sealed class DepartmentCreateRequest : DepartmentWriteRequest { }
public sealed class DepartmentUpdateRequest : DepartmentWriteRequest { }
public sealed class DepartmentSearchRequest
{
    public string? Name { get; set; }
}
public record DepartmentListItem
{
    public int DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;
    public string DepartmentDescription { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
public sealed record DepartmentDetail : DepartmentListItem
{
    public IReadOnlyList<ProjectListItem> Projects { get; init; } = [];
}
