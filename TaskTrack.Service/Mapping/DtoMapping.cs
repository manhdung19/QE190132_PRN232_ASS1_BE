using TaskTrack.Repo.Models;
using TaskTrack.Service.Contracts;
using TaskEntity = TaskTrack.Repo.Models.Task;
using TaskStatus = TaskTrack.Service.Contracts.TaskStatus;

namespace TaskTrack.Service.Mapping;

// Callers must load the required navigation data in Repo before mapping.
// No lazy loading or database queries are performed here.
public static class DtoMapping
{
    public static DepartmentListItem ToListItem(this Department x) => new()
    {
        DepartmentId = x.DepartmentId, DepartmentName = x.DepartmentName,
        DepartmentDescription = x.DepartmentDescription, IsActive = x.IsActive
    };
    public static DepartmentDetail ToDetail(this Department x) => new()
    {
        DepartmentId = x.DepartmentId, DepartmentName = x.DepartmentName,
        DepartmentDescription = x.DepartmentDescription, IsActive = x.IsActive,
        Projects = x.Projects.Where(p => p.IsActive).OrderBy(p => p.ProjectId).Select(p => p.ToListItem()).ToArray()
    };
    public static ProjectListItem ToListItem(this Project x) => new()
    {
        ProjectId = x.ProjectId, ProjectName = x.ProjectName, Description = x.Description,
        StartDate = x.StartDate, EndDate = x.EndDate, Status = (ProjectStatus)x.Status,
        DepartmentId = x.DepartmentId, DepartmentName = x.Department.DepartmentName,
        IsActive = x.IsActive, CreatedDate = x.CreatedDate
    };
    public static ProjectDetail ToDetail(this Project x) => new()
    {
        ProjectId = x.ProjectId, ProjectName = x.ProjectName, Description = x.Description,
        StartDate = x.StartDate, EndDate = x.EndDate, Status = (ProjectStatus)x.Status,
        DepartmentId = x.DepartmentId, DepartmentName = x.Department.DepartmentName,
        IsActive = x.IsActive, CreatedDate = x.CreatedDate,
        Tasks = x.Tasks.Where(t => t.IsActive).OrderBy(t => t.TaskId).Select(t => t.ToListItem()).ToArray()
    };
    public static TagListItem ToListItem(this Tag x) => new() { TagId = x.TagId, TagName = x.TagName, Color = x.Color };
    public static TagDetail ToDetail(this Tag x) => new() { TagId = x.TagId, TagName = x.TagName, Color = x.Color };
    public static TaskListItem ToListItem(this TaskEntity x) => x.ToDetail();
    public static TaskDetail ToDetail(this TaskEntity x) => new()
    {
        TaskId = x.TaskId, Title = x.Title, Description = x.Description, Status = (TaskStatus)x.Status,
        Priority = (TaskPriority)x.Priority, DueDate = x.DueDate, ProjectId = x.ProjectId,
        IsActive = x.IsActive, CreatedDate = x.CreatedDate, ModifiedDate = x.ModifiedDate,
        Tags = x.Tags.OrderBy(t => t.TagId).Select(t => t.ToListItem()).ToArray()
    };
}

public static class DatabaseTime
{
    public static DateTime UtcNow() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
