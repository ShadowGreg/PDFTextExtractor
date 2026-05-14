using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;
using ApiGateway.Services;

namespace ApiGateway.Controllers;

[ApiController]
[Route("[controller]")]
public class DocumentsController(IDocumentService documentService) : ControllerBase
{
    /// <summary>Upload a PDF document for processing</summary>
    /// <response code="201">Returns the ID of the uploaded document</response>
    /// <response code="400">If the file is invalid</response>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Guid>> UploadDocument([FromForm] UploadDocumentRequest request)
    {
        var documentId = await documentService.UploadDocumentAsync(request.File, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetDocument), new { id = documentId }, documentId);
    }

    /// <summary>Get a list of all documents</summary>
    [HttpGet]
    [ProducesResponseType(typeof(DocumentListResponse[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<DocumentListResponse[]>> GetDocuments()
    {
        var documents = await documentService.GetDocumentsAsync(HttpContext.RequestAborted);
        return Ok(documents);
    }

    /// <summary>Get a specific document by ID</summary>
    /// <response code="200">Returns the document details</response>
    /// <response code="404">If the document is not found</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentResponse>> GetDocument(Guid id)
    {
        var document = await documentService.GetDocumentAsync(id, HttpContext.RequestAborted);
        return document is null ? NotFound() : Ok(document);
    }
}
