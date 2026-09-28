using DepartmentEntity = TaskTrack.Repo.Models.Department;
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
