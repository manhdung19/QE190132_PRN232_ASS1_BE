using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TaskTrack.Repo.Data;
using TaskTrack.Repo.Models;
using TaskEntity = TaskTrack.Repo.Models.Task;
using AsyncTask = System.Threading.Tasks.Task;

namespace TaskTrack.Repo.Repositories;

public abstract class Repository<T>(TaskManagementDbContext context) : IRepository<T> where T : class
{
    protected TaskManagementDbContext Context { get; } = context;
    public async Task<T?> FindAsync(int id, CancellationToken cancellationToken = default) =>
        await Context.Set<T>().FindAsync([id], cancellationToken);
    public async Task<IReadOnlyList<T>> ReadAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        await Context.Set<T>().AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    public Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Context.Set<T>().AnyAsync(predicate, cancellationToken);
    public void Add(T entity) => Context.Set<T>().Add(entity);
}
public sealed class DepartmentRepository(TaskManagementDbContext context) : Repository<Department>(context), IDepartmentRepository
{
    public async Task<IReadOnlyList<Department>> GetActiveAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        var query = Context.Departments.AsNoTracking().Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(name))
        {
            // Escape LIKE metacharacters so client input is a literal substring.
            var pattern = "%" + name.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.DepartmentName, pattern, "\\"));
        }
        return await query.OrderBy(x => x.DepartmentId).ToListAsync(cancellationToken);
    }

    public Task<Department?> GetActiveDetailAsync(int id, CancellationToken cancellationToken = default) =>
        Context.Departments.AsNoTracking()
            .Include(x => x.Projects.Where(p => p.IsActive).OrderBy(p => p.ProjectId))
            .SingleOrDefaultAsync(x => x.DepartmentId == id && x.IsActive, cancellationToken);

    public Task<Department?> FindTrackedDetailAsync(int id, CancellationToken cancellationToken = default) =>
        Context.Departments
            .Include(x => x.Projects.Where(p => p.IsActive).OrderBy(p => p.ProjectId))
            .SingleOrDefaultAsync(x => x.DepartmentId == id, cancellationToken);

    public Task<bool> HasProjectsAsync(int id, CancellationToken cancellationToken = default) =>
        Context.Projects.AnyAsync(x => x.DepartmentId == id, cancellationToken);
    public void Remove(Department entity) => Context.Departments.Remove(entity);
}
public sealed class ProjectRepository(TaskManagementDbContext context) : Repository<Project>(context), IProjectRepository
{
    public Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default) =>
        Context.Tasks.AnyAsync(x => x.ProjectId == id, cancellationToken);
    public void Remove(Project entity) => Context.Projects.Remove(entity);
}
public sealed class TagRepository(TaskManagementDbContext context) : Repository<Tag>(context), ITagRepository
{
    public Task<bool> HasTasksAsync(int id, CancellationToken cancellationToken = default) =>
        Context.Tags.AnyAsync(x => x.TagId == id && x.Tasks.Any(), cancellationToken);
    public void Remove(Tag entity) => Context.Tags.Remove(entity);
}
public sealed class TaskRepository(TaskManagementDbContext context) : Repository<TaskEntity>(context), ITaskRepository
{
    public AsyncTask LoadTagsAsync(TaskEntity entity, CancellationToken cancellationToken = default) =>
        Context.Entry(entity).Collection(x => x.Tags).LoadAsync(cancellationToken);
}
