using TaskTrack.Repo.Configuration;
using TaskTrack.Repo.Data;
using Microsoft.EntityFrameworkCore;
using TaskTrack.Repo;
using TaskTrack.Service;
using TaskTrack.API.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.OpenApi.Models;
using TaskTrack.API.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

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
builder.Services.AddSingleton<JwtTokenService>();
var signingKey = new JwtTokenService(builder.Configuration).GetSigningKey();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = "role"
    };
});
builder.Services.AddAuthorization();
builder.Services.AddControllers(options =>
    options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ApiErrors.InvalidModelState);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// CORS configuration reading allowed origins from configuration
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? builder.Configuration["Cors:AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["http://localhost:3000", "http://127.0.0.1:3000"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("TaskTrackCorsPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            {
                if (configuredOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                    return true;
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    if (uri.Host.EndsWith("vercel.app", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host == "localhost" ||
                        uri.Host == "127.0.0.1")
                    {
                        return true;
                    }
                }
                return false;
            })
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
            ### PRN232 Assignment 2 — Task & Team Management API (QE190132)
            
            API for managing Departments, Projects, Tasks, and Tags.
            
            #### Business Rules & Specifications:
            - **Access**: Resource GET endpoints and register/login are public. Resource POST/PUT/DELETE require a Staff or Admin Bearer token. Account API is Admin only.
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
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT returned by POST /api/auth/login."
    });
    c.OperationFilter<BearerSecurityOperationFilter>();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
