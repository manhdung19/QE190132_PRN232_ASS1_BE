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
    Task<bool> HasProjectsAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Department entity);
}
public interface IProjectRepository : IRepository<Project>
{
    Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Project entity);
}
public interface ITagRepository : IRepository<Tag>
{
    Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default);
    void Remove(Tag entity);
}
public interface ITaskRepository : IRepository<TaskEntity>
{
    System.Threading.Tasks.Task LoadTagsAsync(TaskEntity entity, CancellationToken cancellationToken = default);
    // No Remove: tasks may only be soft deleted through tracked IsActive changes.
}
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
