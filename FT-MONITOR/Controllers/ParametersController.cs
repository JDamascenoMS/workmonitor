using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FT_MONITOR.Controllers
{
    [Authorize]
    [Route("/api/parameters")]
    public class ParametersController : ControllerBase
    {
        private readonly ParametersService _parametersService;

        public ParametersController(ParametersService parametersService)
        {
            _parametersService = parametersService;
        }

        [HttpGet("area-paths")]
        public IActionResult GetAreaPaths()
        {
            var paths = _parametersService.GetAreaPaths();
            var result = paths.Select(p => new { name = p.Key, path = p.Value }).ToList();
            return Ok(result);
        }

        [HttpPost("area-paths")]
        public IActionResult AddAreaPath([FromBody] AreaPathDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Name) || string.IsNullOrWhiteSpace(dto?.Path))
                return BadRequest("Name and Path are required.");

            try
            {
                _parametersService.AddAreaPath(dto.Name.Trim(), dto.Path.Trim());
                return Ok(new { message = "Area path added." });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpPut("area-paths/{name}")]
        public IActionResult UpdateAreaPath(string name, [FromBody] AreaPathDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Name) || string.IsNullOrWhiteSpace(dto?.Path))
                return BadRequest("Name and Path are required.");

            try
            {
                _parametersService.UpdateAreaPath(name, dto.Name.Trim(), dto.Path.Trim());
                return Ok(new { message = "Area path updated." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpDelete("area-paths/{name}")]
        public IActionResult DeleteAreaPath(string name)
        {
            try
            {
                _parametersService.DeleteAreaPath(name);
                return Ok(new { message = "Area path deleted." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpGet("saved-queries")]
        public IActionResult GetSavedQueries()
        {
            var queries = _parametersService.GetSavedQueries();
            var result = queries.Select(q => new { name = q.Key, queryId = q.Value }).ToList();
            return Ok(result);
        }

        [HttpPost("saved-queries")]
        public IActionResult AddSavedQuery([FromBody] SavedQueryDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Name) || string.IsNullOrWhiteSpace(dto?.QueryId))
                return BadRequest("Name and QueryId are required.");

            try
            {
                _parametersService.AddSavedQuery(dto.Name.Trim(), dto.QueryId.Trim());
                return Ok(new { message = "Saved query added." });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpPut("saved-queries/{name}")]
        public IActionResult UpdateSavedQuery(string name, [FromBody] SavedQueryDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Name) || string.IsNullOrWhiteSpace(dto?.QueryId))
                return BadRequest("Name and QueryId are required.");

            try
            {
                _parametersService.UpdateSavedQuery(name, dto.Name.Trim(), dto.QueryId.Trim());
                return Ok(new { message = "Saved query updated." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpDelete("saved-queries/{name}")]
        public IActionResult DeleteSavedQuery(string name)
        {
            try
            {
                _parametersService.DeleteSavedQuery(name);
                return Ok(new { message = "Saved query deleted." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }
    }

    public class AreaPathDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public class SavedQueryDto
    {
        public string Name { get; set; }
        public string QueryId { get; set; }
    }
}
