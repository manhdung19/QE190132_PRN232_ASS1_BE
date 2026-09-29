using TaskTrack.Repo.Configuration;
using TaskTrack.Repo.Data;
using Microsoft.EntityFrameworkCore;
using TaskTrack.Repo;
using TaskTrack.Service;
using TaskTrack.API.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var databaseUrl = builder.Configuration["DATABASE_URL"];
if (!string.IsNullOrWhiteSpace(databaseUrl))
{
    builder.Configuration["ConnectionStrings:TaskTrack"] = PostgresConnection.FromUrl(databaseUrl);
}

// Connection is opened only when a repository executes a query.
builder.Services.AddDbContext<TaskManagementDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("TaskTrack");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Set DATABASE_URL or ConnectionStrings__TaskTrack before accessing the database.");
    options.UseNpgsql(connectionString);
});

builder.Services.AddTaskTrackRepositories();
builder.Services.AddTaskTrackServices();
builder.Services.AddControllers(options =>
    options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ApiErrors.InvalidModelState);

// CORS configuration reading allowed origins from configuration
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? builder.Configuration["Cors:AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["http://localhost:3000", "http://127.0.0.1:3000"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("TaskTrackCorsPolicy", policy =>
    {
        policy.WithOrigins(configuredOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TaskTrack Management API",
        Version = "v1",
        Description = """
            ### PRN232 Assignment 1 — Task & Team Management API (QE190132)
            
            Public Web API for managing Departments, Projects, Tasks, and Tags.
            
            #### Business Rules & Specifications:
            - **Public Access**: All 24 endpoints are public and do not require authentication or Bearer tokens.
            - **Enums**:
              - **ProjectStatus**: `0` = NotStarted, `1` = InProgress, `2` = Completed, `3` = OnHold.
              - **TaskStatus**: `0` = ToDo, `1` = InProgress, `2` = Done, `3` = Cancelled.
              - **TaskPriority**: `0` = Low, `1` = Medium, `2` = High, `3` = Critical.
            - **TagIDs in Task operations**:
              - `TagIDs` in Task creation and update is a list of positive integers. Duplicate IDs are automatically deduplicated.
              - `PUT /api/tasks/{id}` replaces the entire set of tags (`tagIds: []` clears all tags).
            - **Deletion & Constraint Rules**:
              - **Department**: Hard delete; blocked with HTTP 400 (`operation`) if any Project is associated.
              - **Project**: Hard delete; blocked with HTTP 400 (`operation`) if any Task (active or inactive) is associated.
              - **Tag**: Hard delete; blocked with HTTP 400 (`operation`) if any Task (active or inactive) is associated.
              - **Task**: **Soft delete only** (`IsActive = false`), never physically removed.
            - **Error Contract (RFC 7807 ProblemDetails)**:
              - 400 Validation Error: Includes a camelCase map `errors` (e.g., `title`, `projectId`, `operation`).
              - 404 Not Found: Standard ProblemDetails for non-existent or inactive resources.
              - 500 Internal Error: Safe generic error message; never leaks stack traces or SQL details.
            """
    });
});

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();

// Enable Swagger in Development or if explicitly enabled via configuration (for evaluation)
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:EnableInProduction"))
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "TaskTrack API v1");
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("TaskTrackCorsPolicy");
app.UseAuthorization();
app.MapControllers();

app.Run();
