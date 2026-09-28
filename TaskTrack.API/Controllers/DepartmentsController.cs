using Microsoft.AspNetCore.Mvc;
using TaskTrack.Service.Contracts;
using TaskTrack.Service.Services;

namespace TaskTrack.API.Controllers;

[ApiController]
[Route("api/departments")]
public sealed class DepartmentsController(IDepartmentService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DepartmentListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DepartmentListItem>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await service.GetAllAsync(cancellationToken));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(DepartmentDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DepartmentDetail>> GetById(int id, CancellationToken cancellationToken) =>
        Ok(await service.GetByIdAsync(id, cancellationToken));

    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<DepartmentListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DepartmentListItem>>> Search(
        [FromQuery] DepartmentSearchRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SearchAsync(request, cancellationToken));
}
