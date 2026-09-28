using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;

namespace TaskTrack.Service.Services;

// Foundation contracts. CRUD/search methods are added with tasks 005–015.
public interface IDepartmentService
{
    void Validate(DepartmentWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface IProjectService
{
    void Validate(ProjectWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface ITagService
{
    void Validate(TagWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public interface ITaskService
{
    void Validate(TaskWriteRequest request);
    Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default);
}
public sealed class DepartmentService(IDepartmentRepository repository) : IDepartmentService
{
    public void Validate(DepartmentWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.DepartmentId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class ProjectService(IProjectRepository repository) : IProjectService
{
    public void Validate(ProjectWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.ProjectId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class TagService(ITagRepository repository) : ITagService
{
    public void Validate(TagWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.TagId == id, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
public sealed class TaskService(ITaskRepository repository) : ITaskService
{
    public void Validate(TaskWriteRequest request) => RequestValidation.Validate(request);
    public async Task EnsureExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await repository.ExistsAsync(x => x.TaskId == id && x.IsActive, cancellationToken))
            throw new ServiceException(ServiceErrorKind.NotFound);
    }
}
