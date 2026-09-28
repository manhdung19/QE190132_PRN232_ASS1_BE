using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskTrack.Repo.Data;

namespace TaskTrack.Repo.Repositories;

public enum PersistenceConflictKind { ForeignKey, Unique }

// Contains no provider exception or database details; safe to translate at the API boundary.
public sealed class PersistenceConflictException(PersistenceConflictKind kind, string field)
    : Exception("The operation conflicts with related or existing data.")
{
    public PersistenceConflictKind Kind { get; } = kind;
    public string Field { get; } = field;
}

public sealed class UnitOfWork(TaskManagementDbContext context) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg &&
            pg.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.UniqueViolation)
        {
            var error = (PostgresException)ex.InnerException!;
            // A delete FK conflict belongs to the operation, not a payload FK field.
            var deleting = ex.Entries.Any(e => e.State == EntityState.Deleted);
            var field = deleting ? "operation" : error.ConstraintName switch
            {
                "FK_Project_Department" => "departmentId",
                "FK_Task_Project" => "projectId",
                "FK_TaskTag_Tag" => "tagIds",
                "Tag_TagName_key" => "tagName",
                _ => "operation"
            };
            throw new PersistenceConflictException(
                error.SqlState == PostgresErrorCodes.UniqueViolation ? PersistenceConflictKind.Unique : PersistenceConflictKind.ForeignKey,
                field);
        }
    }
}
