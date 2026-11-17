using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;

namespace Jellyfin.Plugin.SpaceSaver;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SpaceSaver")]
public sealed class SpaceSaverController(MediaCatalog catalog, ConversionService conversions) : ControllerBase
{
    [HttpGet("Media")]
    public ActionResult<object> Media([FromQuery] string sort = "size", [FromQuery] bool descending = true,
        [FromQuery] string? search = null, [FromQuery] int start = 0, [FromQuery] int limit = 100)
    {
        IEnumerable<MediaRow> rows = catalog.List();
        if (!string.IsNullOrWhiteSpace(search)) rows = rows.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
        var all = rows.ToArray();
        rows = sort switch { "name" => descending ? all.OrderByDescending(r => r.Name) : all.OrderBy(r => r.Name), "codec" => descending ? all.OrderByDescending(r => r.Codec) : all.OrderBy(r => r.Codec), _ => descending ? all.OrderByDescending(r => r.Size) : all.OrderBy(r => r.Size) };
        return Ok(new { Items = rows.Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 500)), TotalCount = all.Length, TotalBytes = all.Sum(r => r.Size) });
    }
    [HttpGet("Jobs")]
    public ActionResult<ConversionJob[]> Jobs() => conversions.List();
    [HttpPost("Jobs/{itemId:guid}")]
    public ActionResult<ConversionJob> Enqueue(Guid itemId)
    {
        try { return Ok(conversions.Enqueue(itemId)); } catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Jobs/{id:guid}/Cancel")]
    public ActionResult Cancel(Guid id)
    {
        try { conversions.Cancel(id); return NoContent(); } catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpDelete("Jobs/{id:guid}/Backup")]
    public async Task<ActionResult> DeleteBackup(Guid id, CancellationToken token)
    {
        try { await conversions.DeleteBackup(id, token); return NoContent(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Capabilities")]
    public async Task<ActionResult<object>> Capabilities(CancellationToken token) => Ok(await conversions.Capabilities(token));
    [HttpPost("TestGpu")]
    public async Task<ActionResult<object>> TestGpu(CancellationToken token)
    {
        try { return Ok(await conversions.TestGpu(token)); } catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { return BadRequest(new { Message = ex.Message }); }
    }
}
