using DepartmentEntity = TaskTrack.Repo.Models.Department;
using ProjectEntity = TaskTrack.Repo.Models.Project;
using TagEntity = TaskTrack.Repo.Models.Tag;
using TaskEntity = TaskTrack.Repo.Models.Task;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Service.Mapping;

namespace TaskTrack.Service.Services;

// Foundation contracts. CRUD/search methods are added with tasks 005–015.
public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentListItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DepartmentDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartmentListItem>> SearchAsync(DepartmentSearchRequest request, CancellationToken cancellationToken = default);
    Task<DepartmentDetail> CreateAsync(DepartmentCreateRequest request, CancellationToken cancellationToken = default);
    Task<DepartmentDetail> UpdateAsync(int id, DepartmentUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    void Validate(DepartmentWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface IProjectService
{
    Task<IReadOnlyList<ProjectListItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProjectDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectListItem>> GetByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectListItem>> SearchAsync(ProjectSearchRequest request, CancellationToken cancellationToken = default);
    Task<ProjectDetail> CreateAsync(ProjectCreateRequest request, CancellationToken cancellationToken = default);
    Task<ProjectDetail> UpdateAsync(int id, ProjectUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    void Validate(ProjectWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface ITagService
{
    Task<IReadOnlyList<TagListItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TagDetail> CreateAsync(TagCreateRequest request, CancellationToken cancellationToken = default);
    Task<TagDetail> UpdateAsync(int id, TagUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    void Validate(TagWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface ITaskService
{
    Task<IReadOnlyList<TaskListItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TaskDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(int projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskListItem>> SearchAsync(TaskSearchRequest request, CancellationToken cancellationToken = default);
    Task<TaskDetail> CreateAsync(TaskCreateRequest request, CancellationToken cancellationToken = default);
    Task<TaskDetail> UpdateAsync(int id, TaskUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    void Validate(TaskWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public sealed class DepartmentService(IDepartmentRepository repository, IUnitOfWork unitOfWork) : IDepartmentService
{
    public async Task<IReadOnlyList<DepartmentListItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetActiveAsync(cancellationToken: cancellationToken)).Select(x => x.ToListItem()).ToArray();

    public async Task<DepartmentDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var department = await repository.GetActiveDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        return department.ToDetail();
    }

    public async Task<IReadOnlyList<DepartmentListItem>> SearchAsync(DepartmentSearchRequest request, CancellationToken cancellationToken = default) =>
        (await repository.GetActiveAsync(request.Name, cancellationToken)).Select(x => x.ToListItem()).ToArray();

    public async Task<DepartmentDetail> CreateAsync(DepartmentCreateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entity = new DepartmentEntity
        {
            DepartmentName = request.DepartmentName,
            DepartmentDescription = request.DepartmentDescription,
            IsActive = request.IsActive
        };
        repository.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task<DepartmentDetail> UpdateAsync(int id, DepartmentUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entity = await repository.FindTrackedDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        entity.DepartmentName = request.DepartmentName;
        entity.DepartmentDescription = request.DepartmentDescription;
        entity.IsActive = request.IsActive;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        if (await repository.HasProjectsAsync(id, cancellationToken))
            throw ServiceException.Blocked("Cannot delete department with associated projects.");

        repository.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public void Validate(DepartmentWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.DepartmentId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class ProjectService(
    IProjectRepository repository,
    IDepartmentRepository departmentRepository,
    IUnitOfWork unitOfWork) : IProjectService
{
    public async Task<IReadOnlyList<ProjectListItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetActiveAsync(cancellationToken)).Select(x => x.ToListItem()).ToArray();

    public async Task<ProjectDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var project = await repository.GetActiveDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        return project.ToDetail();
    }

    public async Task<IReadOnlyList<ProjectListItem>> GetByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default)
    {
        if (!await departmentRepository.ExistsAsync(x => x.DepartmentId == departmentId && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
        return (await repository.GetActiveByDepartmentAsync(departmentId, cancellationToken)).Select(x => x.ToListItem()).ToArray();
    }

    public async Task<IReadOnlyList<ProjectListItem>> SearchAsync(ProjectSearchRequest request, CancellationToken cancellationToken = default)
    {
        RequestValidation.Validate(request);
        var status = request.Status.HasValue ? (short)request.Status.Value : (short?)null;
        var list = await repository.SearchActiveAsync(request.Name, status, request.DepartmentId, cancellationToken);
        return list.Select(x => x.ToListItem()).ToArray();
    }

    public async Task<ProjectDetail> CreateAsync(ProjectCreateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var dept = await departmentRepository.FindAsync(request.DepartmentId, cancellationToken);
        if (dept is null || !dept.IsActive)
            throw ServiceException.Invalid("departmentId", "The specified department does not exist or is inactive.");

        var entity = new ProjectEntity
        {
            ProjectName = request.ProjectName,
            Description = request.Description,
            StartDate = request.StartDate!.Value,
            EndDate = request.EndDate,
            Status = (short)request.Status,
            DepartmentId = request.DepartmentId,
            Department = dept,
            IsActive = request.IsActive,
            CreatedDate = DatabaseTime.UtcNow()
        };
        repository.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task<ProjectDetail> UpdateAsync(int id, ProjectUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entity = await repository.FindTrackedDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        var dept = await departmentRepository.FindAsync(request.DepartmentId, cancellationToken);
        if (dept is null || !dept.IsActive)
            throw ServiceException.Invalid("departmentId", "The specified department does not exist or is inactive.");

        entity.ProjectName = request.ProjectName;
        entity.Description = request.Description;
        entity.StartDate = request.StartDate!.Value;
        entity.EndDate = request.EndDate;
        entity.Status = (short)request.Status;
        entity.DepartmentId = request.DepartmentId;
        entity.Department = dept;
        entity.IsActive = request.IsActive;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        if (await repository.HasTasksAsync(id, cancellationToken))
            throw ServiceException.Blocked("Cannot delete project with associated tasks.");

        repository.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public void Validate(ProjectWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.ProjectId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class TagService(ITagRepository repository, IUnitOfWork unitOfWork) : ITagService
{
    public async Task<IReadOnlyList<TagListItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetAllAsync(cancellationToken)).Select(x => x.ToListItem()).ToArray();

    public async Task<TagDetail> CreateAsync(TagCreateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (await repository.ExistsAsync(x => x.TagName == request.TagName, cancellationToken))
            throw ServiceException.Invalid("tagName", "The tag name already exists.");

        var entity = new TagEntity
        {
            TagName = request.TagName,
            Color = request.Color
        };
        repository.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task<TagDetail> UpdateAsync(int id, TagUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entity = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);

        if (await repository.ExistsAsync(x => x.TagName == request.TagName && x.TagId != id, cancellationToken))
            throw ServiceException.Invalid("tagName", "The tag name already exists.");

        entity.TagName = request.TagName;
        entity.Color = request.Color;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);

        if (await repository.HasTasksAsync(id, cancellationToken))
            throw ServiceException.Blocked("Cannot delete tag that is associated with tasks.");

        repository.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public void Validate(TagWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.TagId == id, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class TaskService(
    ITaskRepository repository,
    IProjectRepository projectRepository,
    ITagRepository tagRepository,
    IUnitOfWork unitOfWork) : ITaskService
{
    public async Task<IReadOnlyList<TaskListItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetActiveAsync(cancellationToken)).Select(x => x.ToListItem()).ToArray();

    public async Task<TaskDetail> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await repository.GetActiveDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);
        return task.ToDetail();
    }

    public async Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        if (!await projectRepository.ExistsAsync(x => x.ProjectId == projectId && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
        return (await repository.GetActiveByProjectAsync(projectId, cancellationToken)).Select(x => x.ToListItem()).ToArray();
    }

    public async Task<IReadOnlyList<TaskListItem>> SearchAsync(TaskSearchRequest request, CancellationToken cancellationToken = default)
    {
        RequestValidation.Validate(request);
        var status = request.Status.HasValue ? (short)request.Status.Value : (short?)null;
        var priority = request.Priority.HasValue ? (short)request.Priority.Value : (short?)null;
        return (await repository.SearchActiveAsync(request.Title, status, priority, request.ProjectId, request.TagId, cancellationToken))
            .Select(x => x.ToListItem()).ToArray();
    }

    public async Task<TaskDetail> CreateAsync(TaskCreateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        if (!await projectRepository.ExistsAsync(x => x.ProjectId == request.ProjectId && x.IsActive, cancellationToken))
            throw ServiceException.Invalid("projectId", "The specified project does not exist or is inactive.");

        var tagIds = request.TagIds ?? [];
        IReadOnlyList<TagEntity> trackedTags = [];
        if (tagIds.Length > 0)
        {
            trackedTags = await tagRepository.GetTrackedByIdsAsync(tagIds, cancellationToken);
            if (trackedTags.Count != tagIds.Length)
                throw ServiceException.Invalid("tagIds", "One or more tag IDs do not exist.");
        }

        var entity = new TaskEntity
        {
            Title = request.Title,
            Description = request.Description,
            Status = (short)request.Status,
            Priority = (short)request.Priority,
            DueDate = request.DueDate,
            ProjectId = request.ProjectId,
            IsActive = true,
            CreatedDate = DatabaseTime.UtcNow(),
            ModifiedDate = null
        };

        foreach (var tag in trackedTags)
        {
            entity.Tags.Add(tag);
        }

        repository.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task<TaskDetail> UpdateAsync(int id, TaskUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var entity = await repository.FindTrackedDetailAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);

        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        if (!await projectRepository.ExistsAsync(x => x.ProjectId == request.ProjectId && x.IsActive, cancellationToken))
            throw ServiceException.Invalid("projectId", "The specified project does not exist or is inactive.");

        var tagIds = request.TagIds ?? [];
        IReadOnlyList<TagEntity> trackedTags = [];
        if (tagIds.Length > 0)
        {
            trackedTags = await tagRepository.GetTrackedByIdsAsync(tagIds, cancellationToken);
            if (trackedTags.Count != tagIds.Length)
                throw ServiceException.Invalid("tagIds", "One or more tag IDs do not exist.");
        }

        entity.Tags.Clear();
        foreach (var tag in trackedTags)
        {
            entity.Tags.Add(tag);
        }

        entity.Title = request.Title;
        entity.Description = request.Description;
        entity.Status = (short)request.Status;
        entity.Priority = (short)request.Priority;
        entity.DueDate = request.DueDate;
        entity.ProjectId = request.ProjectId;
        entity.ModifiedDate = DatabaseTime.UtcNow();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return entity.ToDetail();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await repository.FindAsync(id, cancellationToken)
            ?? throw new ServiceException(ServiceErrorKind.NotFound);

        if (!entity.IsActive)
            throw new ServiceException(ServiceErrorKind.NotFound);

        entity.IsActive = false;
        entity.ModifiedDate = DatabaseTime.UtcNow();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public void Validate(TaskWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.TaskId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}

