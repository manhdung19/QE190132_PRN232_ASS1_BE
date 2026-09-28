using Microsoft.Extensions.DependencyInjection;
using TaskTrack.Service.Services;

namespace TaskTrack.Service;

public static class DependencyInjection
{
    public static IServiceCollection AddTaskTrackServices(this IServiceCollection services)
    {
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<ITaskService, TaskService>();
        return services;
    }
}
