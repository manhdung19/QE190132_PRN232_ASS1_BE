using System.Linq.Expressions;
using TaskTrack.Repo.Models;
using TaskEntity = TaskTrack.Repo.Models.Task;

namespace TaskTrack.Repo.Repositories;

// Infrastructure only. Endpoint-specific filters/includes are added in tasks 005–015.
// Expressions execute inside Repo; IQueryable never crosses this boundary.
public interface IRepository<T> where T : class
{
    Task<T?> FindAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ReadAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    void Add(T entity);
    // FindAsync returns a tracked entity: edit its properties to stage updates.
}
public interface IDepartmentRepository : IRepository<Department>
{
    Task<IReadOnlyList<Department>> GetActiveAsync(string? name = null, CancellationToken cancellationToken = default);
    Task<Department?> GetActiveDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<Department?> FindTrackedDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> HasProjectsAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Department entity);
}
public interface IProjectRepository : IRepository<Project>
{
    Task<IReadOnlyList<Project>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetActiveDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<Project?> FindTrackedDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> GetActiveByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> SearchActiveAsync(string? name, short? status, int? departmentId, CancellationToken cancellationToken = default);
    Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Project entity);
}
public interface ITagRepository : IRepository<Tag>
{
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tag>> GetTrackedByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);
    Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Tag entity);
}
public interface ITaskRepository : IRepository<TaskEntity>
{
    Task<IReadOnlyList<TaskEntity>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<TaskEntity?> GetActiveDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<TaskEntity?> FindTrackedDetailAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskEntity>> GetActiveByProjectAsync(int projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskEntity>> SearchActiveAsync(string? title, short? status, short? priority, int? projectId, int? tagId, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task LoadTagsAsync(TaskEntity entity, CancellationToken cancellationToken = default);
    // No Remove: tasks may only be soft deleted through tracked IsActive changes.
}
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
