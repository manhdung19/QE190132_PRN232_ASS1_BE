using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TaskTrack.API.Errors;
using TaskTrack.Repo;
using TaskTrack.Repo.Data;
using TaskTrack.Repo.Repositories;
using TaskTrack.Service;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Errors;
using TaskTrack.Service.Mapping;
using TaskTrack.Service.Services;

var count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + label);
    count++;
}
void Invalid(object request, string field)
{
    try { RequestValidation.Validate(request); throw new Exception("Expected validation error: " + field); }
    catch (ServiceException ex) { Check(ex.Errors.ContainsKey(field), "Validation field " + field); }
}

RequestValidation.Validate(new DepartmentCreateRequest { DepartmentName = "Engineering", DepartmentDescription = "Software" });
RequestValidation.Validate(new ProjectCreateRequest { ProjectName = "Portal", DepartmentId = 1, StartDate = new(2020, 1, 1) });
RequestValidation.Validate(new TagCreateRequest { TagName = "Backend", Color = "#123ABC" });
RequestValidation.Validate(new TaskCreateRequest { Title = "API", ProjectId = 1, DueDate = new(2000, 1, 1) });
Invalid(new DepartmentCreateRequest { DepartmentName = "  ", DepartmentDescription = "ok" }, "departmentName");
Invalid(new DepartmentUpdateRequest { DepartmentName = new string('a', 101), DepartmentDescription = "ok" }, "departmentName");
Invalid(new DepartmentCreateRequest { DepartmentName = "ok", DepartmentDescription = new string('a', 301) }, "departmentDescription");
Invalid(new ProjectCreateRequest { ProjectName = "ok", DepartmentId = 1 }, "startDate");
Invalid(new ProjectCreateRequest { ProjectName = "ok", DepartmentId = 1, StartDate = new(2026, 9, 28), EndDate = new(2026, 9, 27) }, "endDate");
Invalid(new ProjectCreateRequest { ProjectName = "ok", DepartmentId = 1, StartDate = new(2026, 9, 28), Status = (ProjectStatus)4 }, "status");
Invalid(new TagCreateRequest { TagName = "ok", Color = "red" }, "color");
Invalid(new TagCreateRequest { TagName = " " }, "tagName");
Invalid(new TaskCreateRequest { Title = "ok", ProjectId = 0 }, "projectId");
Invalid(new TaskCreateRequest { Title = "ok", ProjectId = 1, Priority = (TaskPriority)(-1) }, "priority");
Invalid(new TaskCreateRequest { Title = "ok", ProjectId = 1, TagIds = [0] }, "tagIds");
Invalid(new TaskSearchRequest { TagId = 0 }, "tagId");
var taskRequest = new TaskUpdateRequest { TagIds = [1, 1, 2] };
Check(taskRequest.TagIds!.SequenceEqual([1, 2]), "Deduplicate tags");
taskRequest.TagIds = null;
Check(taskRequest.TagIds!.Length == 0, "Null tags means empty");
Check(new TaskCreateRequest().Priority == TaskPriority.Medium && new TaskCreateRequest().IsActive, "SQL defaults");
Check(DatabaseTime.UtcNow().Kind == DateTimeKind.Unspecified, "Timestamp kind");
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var ignored = JsonSerializer.Deserialize<TaskCreateRequest>("{\"title\":\"ok\",\"projectId\":1,\"createdDate\":\"2000-01-01\"}", jsonOptions)!;
Check(!JsonSerializer.Serialize(ignored, jsonOptions).Contains("createdDate"), "Server timestamps not writable");
var entity = new TaskTrack.Repo.Models.Task { TaskId = 1, Title = "ok", Tags = [new() { TagId = 2, TagName = "tag" }] };
var mappedJson = JsonSerializer.Serialize(entity.ToDetail(), jsonOptions);
Check(mappedJson.Contains("\"tagId\":2") && !mappedJson.Contains("\"project\":"), "DTO mapping has tags and no navigation cycles");

// Provider failures are simulated; this tool never opens a database connection.
foreach (var (state, constraint, expectedField) in new[] {
    (PostgresErrorCodes.UniqueViolation, "Tag_TagName_key", "tagName"),
    (PostgresErrorCodes.ForeignKeyViolation, "FK_Task_Project", "projectId"),
    (PostgresErrorCodes.ForeignKeyViolation, "FK_TaskTag_Tag", "tagIds") })
{
    using var db = new FailingContext(new DbUpdateException("private SQL", new PostgresException("private detail", "ERROR", "ERROR", state, constraintName: constraint)));
    try { await new UnitOfWork(db).SaveChangesAsync(); throw new Exception("Expected persistence conflict"); }
    catch (PersistenceConflictException ex) { Check(ex.Field == expectedField && ex.InnerException is null, "Safe constraint mapping"); }
}
using (var db = new FailingContext(new InvalidOperationException("unexpected")))
{
    try { await new UnitOfWork(db).SaveChangesAsync(); throw new Exception("Expected unexpected failure"); }
    catch (InvalidOperationException) { Check(true, "Unexpected errors preserved"); }
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDbContext<TaskManagementDbContext>(options => options.UseNpgsql("Host=localhost;Database=not_used;Username=not_used"));
builder.Services.AddTaskTrackRepositories().AddTaskTrackServices();
builder.Services.AddControllers(options => options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .AddApplicationPart(typeof(ProbeController).Assembly)
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ApiErrors.InvalidModelState);
await using var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
app.MapControllers();
using (var scope = app.Services.CreateScope())
{
    foreach (var type in new[] { typeof(IDepartmentRepository), typeof(IProjectRepository), typeof(ITagRepository), typeof(ITaskRepository), typeof(IUnitOfWork), typeof(IDepartmentService), typeof(IProjectService), typeof(ITagService), typeof(ITaskService) })
        Check(ReferenceEquals(scope.ServiceProvider.GetRequiredService(type), scope.ServiceProvider.GetRequiredService(type)), "Scoped DI " + type.Name);
    var anotherScope = app.Services.CreateScope();
    using (anotherScope)
        Check(!ReferenceEquals(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), anotherScope.ServiceProvider.GetRequiredService<IUnitOfWork>()), "Separate scopes");
    var db = scope.ServiceProvider.GetRequiredService<TaskManagementDbContext>();
    var department = new TaskTrack.Repo.Models.Department { DepartmentName = "test", DepartmentDescription = "test" };
    scope.ServiceProvider.GetRequiredService<IDepartmentRepository>().Add(department);
    Check(db.Entry(department).State == EntityState.Added, "Repository shares scoped DbContext");
    var stagedTask = new TaskTrack.Repo.Models.Task { Title = "test", ProjectId = 1 };
    var stagedTag = new TaskTrack.Repo.Models.Tag { TagName = "test" };
    scope.ServiceProvider.GetRequiredService<ITagRepository>().Add(stagedTag);
    stagedTask.Tags.Add(stagedTag);
    scope.ServiceProvider.GetRequiredService<ITaskRepository>().Add(stagedTask);
    db.ChangeTracker.DetectChanges();
    Check(db.ChangeTracker.Entries().Any(e => e.Metadata.Name == "TaskTag" && e.State == EntityState.Added), "Task and tag join staged together");
}
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    foreach (var (body, field) in new[] {
        ("{\"title\":\" \",\"projectId\":1}", "title"),
        ("{\"title\":\"ok\",\"projectId\":1,\"priority\":8}", "priority"),
        ("{\"title\":\"ok\",\"projectId\":1,\"tagIds\":[-1]}", "tagIds"),
        ("{\"title\":\"ok\",\"projectId\":1,\"dueDate\":\"invalid\"}", "dueDate") })
    {
        using var response = await client.PostAsync("/probe", new StringContent(body, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.BadRequest && doc.RootElement.GetProperty("errors").TryGetProperty(field, out _), "HTTP field " + field);
        Check(response.Content.Headers.ContentType!.MediaType == "application/problem+json", "Problem media type");
    }
    using var valid = await client.PostAsJsonAsync("/probe", new TaskCreateRequest { Title = "ok", ProjectId = 1 });
    Check(valid.StatusCode == HttpStatusCode.OK, "Public valid request");
    foreach (var (kind, status) in new[] { ("missing", 404), ("fk", 400), ("blocked", 400), ("unique", 400), ("unexpected", 500) })
    {
        using var response = await client.GetAsync("/probe/" + kind);
        var body = await response.Content.ReadAsStringAsync();
        Check((int)response.StatusCode == status && !body.Contains("SECRET") && body.Contains("traceId"), "HTTP safe error " + kind);
        if (status == 400) Check(body.Contains("errors"), "HTTP business errors serialized");
    }
}
finally { await app.StopAsync(); }
Console.WriteLine($"PASS: {count} foundation checks; no database connection or writes.");

public sealed class FailingContext(Exception failure) : TaskManagementDbContext(
    new DbContextOptionsBuilder<TaskManagementDbContext>().UseNpgsql("Host=localhost;Database=not_used;Username=not_used").Options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromException<int>(failure);
}

[ApiController]
[Route("probe")]
public sealed class ProbeController : ControllerBase
{
    [HttpPost] public IActionResult Validate(TaskCreateRequest request) => Ok(request);
    [HttpGet("{kind}")] public IActionResult Fail(string kind) => throw (kind switch
    {
        "missing" => new ServiceException(ServiceErrorKind.NotFound),
        "fk" => ServiceException.Invalid("projectId", "Project does not exist."),
        "blocked" => ServiceException.Blocked("Resource has children."),
        "unique" => new PersistenceConflictException(PersistenceConflictKind.Unique, "tagName"),
        _ => new Exception("SECRET SQL connection information")
    });
}
