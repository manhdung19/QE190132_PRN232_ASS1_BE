using Microsoft.AspNetCore.Mvc;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Services;

namespace TaskTrack.API.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(IProjectService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProjectListItem>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await service.GetAllAsync(cancellationToken));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProjectDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDetail>> GetById(int id, CancellationToken cancellationToken) =>
        Ok(await service.GetByIdAsync(id, cancellationToken));

    [HttpGet("department/{departmentId:int}")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ProjectListItem>>> GetByDepartment(
        int departmentId, CancellationToken cancellationToken) =>
        Ok(await service.GetByDepartmentAsync(departmentId, cancellationToken));

    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<ProjectListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProjectListItem>>> Search(
        [FromQuery] ProjectSearchRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SearchAsync(request, cancellationToken));
}
