using TaskTrack.Repo.Configuration;
using TaskTrack.Repo.Data;
using Microsoft.EntityFrameworkCore;
using TaskTrack.Repo;
using TaskTrack.Service;
using TaskTrack.API.Errors;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

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
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
