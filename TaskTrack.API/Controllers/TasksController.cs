using Microsoft.AspNetCore.Mvc;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Services;

namespace TaskTrack.API.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(ITaskService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TaskListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TaskListItem>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await service.GetAllAsync(cancellationToken));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TaskDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskDetail>> GetById(int id, CancellationToken cancellationToken) =>
        Ok(await service.GetByIdAsync(id, cancellationToken));

    [HttpGet("project/{projectId:int}")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<TaskListItem>>> GetByProject(
        int projectId, CancellationToken cancellationToken) =>
        Ok(await service.GetByProjectAsync(projectId, cancellationToken));

    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<TaskListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<TaskListItem>>> Search(
        [FromQuery] TaskSearchRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SearchAsync(request, cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(TaskDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TaskDetail>> Create(
        [FromBody] TaskCreateRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.TaskId }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(TaskDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskDetail>> Update(
        int id, [FromBody] TaskUpdateRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
