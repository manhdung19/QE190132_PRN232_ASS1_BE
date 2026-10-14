using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskTrack.Repo.Data;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Service.Services;

var connectionString = Environment.GetEnvironmentVariable("A2_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set A2_TEST_CONNECTION for a disposable local PostgreSQL database.");
var connection = new NpgsqlConnectionStringBuilder(connectionString);
if (connection.Host is not ("127.0.0.1" or "localhost") || connection.Database != "tasktrack_a2_test")
    throw new InvalidOperationException("This check only runs against local database tasktrack_a2_test.");
connection.Pooling = false;

var options = new DbContextOptionsBuilder<TaskManagementDbContext>()
    .UseNpgsql(connection.ConnectionString).Options;
await using var db = new TaskManagementDbContext(options);
await using var transaction = await db.Database.BeginTransactionAsync();

var accountRepository = new AccountRepository(db);
var taskRepository = new TaskRepository(db);
var accountService = new AccountService(accountRepository, new UnitOfWork(db));
var taskService = new TaskService(
    taskRepository, new ProjectRepository(db), new TagRepository(db),
    accountRepository, new UnitOfWork(db));

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var legacyTask = await taskRepository.FindAsync(1);
Check(legacyTask is not null && legacyTask.CreatedById is null,
    "Legacy Task must remain readable with no creator.");

var created = await accountService.CreateStaffAsync(
    "Mapping Test", "  MAPPING-TEST@EXAMPLE.INVALID  ", "test-only-hash");
Check(created.AccountId > 0 && created.Email == "mapping-test@example.invalid" && created.Role == 0,
    "Account identity, normalization or Staff role is wrong.");
Check(!JsonSerializer.Serialize(created).Contains("PasswordHash", StringComparison.OrdinalIgnoreCase),
    "Account API response exposed the password hash.");
Check((await accountRepository.FindByEmailAsync(created.Email))?.AccountId == created.AccountId,
    "Account email lookup failed.");
Check((await accountService.GetAllAsync()).Any(x => x.AccountId == created.AccountId),
    "Account list failed.");
Check((await accountService.GetByIdAsync(created.AccountId)).Email == created.Email,
    "Account detail lookup failed.");

var disposable = await accountService.CreateStaffAsync(
    "Disposable", "disposable@example.invalid", "test-only-hash");
var updated = await accountService.UpdateAsync(disposable.AccountId, "Updated Name", 1);
Check(updated.FullName == "Updated Name" && updated.Role == 1,
    "Account update failed.");
try
{
    await accountService.UpdateAsync(disposable.AccountId, null, 0);
    throw new InvalidOperationException("Last Admin was demoted.");
}
catch (ServiceException ex) when (ex.Kind == ServiceErrorKind.Blocked)
{
    // Expected until another Admin exists.
}
var removable = await accountService.CreateStaffAsync(
    "Removable", "removable@example.invalid", "test-only-hash");
await accountService.DeleteAsync(removable.AccountId, actingAccountId: created.AccountId);
Check(!await accountRepository.ExistsAsync(x => x.AccountId == removable.AccountId),
    "Account without Tasks was not deleted.");

var newTask = await taskService.CreateAsync(new TaskCreateRequest
{
    Title = "qa-a2-account-mapping",
    ProjectId = 1
}, created.AccountId);
var persisted = await taskRepository.FindAsync(newTask.TaskId);
Check(persisted?.CreatedById == created.AccountId,
    "Task creator was not persisted from the service argument.");
Check(await accountRepository.HasCreatedTasksAsync(created.AccountId),
    "Account-to-Task lookup failed.");

await taskService.DeleteAsync(newTask.TaskId);
Check(await accountRepository.HasCreatedTasksAsync(created.AccountId),
    "Soft-deleted Task no longer protects its creator account.");
try
{
    await accountService.DeleteAsync(created.AccountId, actingAccountId: 0);
    throw new InvalidOperationException("Account with a Task was deleted.");
}
catch (ServiceException ex) when (ex.Kind == ServiceErrorKind.Blocked)
{
    // Expected: Task ownership blocks deletion.
}

await transaction.RollbackAsync();
Console.WriteLine("PASS: account mapping, safe DTO, account CRUD, Task ownership, soft-delete guard; test transaction rolled back.");
