using Microsoft.Extensions.DependencyInjection;
using TaskTrack.Repo.Repositories;

namespace TaskTrack.Repo;

public static class DependencyInjection
{
    public static IServiceCollection AddTaskTrackRepositories(this IServiceCollection services)
    {
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }
}
