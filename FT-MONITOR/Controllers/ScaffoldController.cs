using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text.Json;

namespace FT_MONITOR.Controllers
{
    [Authorize]
    [Route("/api/scaffold")]
    public class ScaffoldController : ControllerBase
    {
        private readonly AzureOpenAIService _aiService;

        public ScaffoldController(AzureOpenAIService aiService)
        {
            _aiService = aiService;
        }

        [HttpPost("generate")]
        public async Task<IActionResult> GenerateProject([FromBody] ScaffoldRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Instructions))
                return BadRequest("Instructions are required.");

            try
            {
                var prompt = $"Generate a .NET 8 project based on these instructions: {request.Instructions}\n\n" +
                             "Return a JSON array of objects with 'path' (relative file path) and 'content' (file content). " +
                             "Include .csproj, Program.cs, and all necessary files. No markdown, just raw JSON.";

                var projectJson = await _aiService.GenerateRawCompletion(prompt);

                // Strip markdown fences if the AI included them
                projectJson = projectJson.Trim();
                if (projectJson.StartsWith("```"))
                {
                    var firstNewline = projectJson.IndexOf('\n');
                    if (firstNewline > 0)
                        projectJson = projectJson[(firstNewline + 1)..];
                    if (projectJson.EndsWith("```"))
                        projectJson = projectJson[..^3];
                    projectJson = projectJson.Trim();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var files = JsonSerializer.Deserialize<List<ScaffoldFile>>(projectJson, options);

                if (files == null || files.Count == 0)
                    return BadRequest("AI did not return valid project files.");

                // Filter out entries with empty paths
                files = files.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();

                if (files.Count == 0)
                    return BadRequest("AI returned files but none had valid paths.");

                using var memoryStream = new MemoryStream();
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    foreach (var file in files)
                    {
                        var entryPath = file.Path.TrimStart('/').TrimStart('\\');
                        var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        using var writer = new StreamWriter(entryStream);
                        await writer.WriteAsync(file.Content ?? "");
                    }
                }

                memoryStream.Position = 0;
                return File(memoryStream.ToArray(), "application/zip", $"{request.ProjectName ?? "generated-project"}.zip");
            }
            catch (JsonException)
            {
                return BadRequest("AI returned invalid JSON. Please try again with a simpler description.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Generation failed: {ex.Message}");
            }
        }
    }

    public class ScaffoldRequest
    {
        public string Instructions { get; set; } = "";
        public string ProjectName { get; set; } = "MyProject";
    }

    public class ScaffoldFile
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }
}