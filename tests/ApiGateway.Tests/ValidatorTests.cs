using ApiGateway.Validators;
using Microsoft.AspNetCore.Http;
using Moq;
using Shared.Contracts;

namespace ApiGateway.Tests;

public class ValidatorTests
{
    private readonly UploadDocumentRequestValidator _validator = new();

    [Fact]
    public async Task Validator_Fails_WhenFileIsNull()
    {
        var request = new UploadDocumentRequest { File = null! };
        var result = await _validator.ValidateAsync(request);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validator_Fails_WhenFileIsEmpty()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(0);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");

        var request = new UploadDocumentRequest { File = fileMock.Object };
        var result = await _validator.ValidateAsync(request);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validator_Fails_WhenNotPdf()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1024);
        fileMock.Setup(f => f.ContentType).Returns("image/jpeg");

        var request = new UploadDocumentRequest { File = fileMock.Object };
        var result = await _validator.ValidateAsync(request);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validator_Passes_WhenValidPdf()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(1024);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");

        var request = new UploadDocumentRequest { File = fileMock.Object };
        var result = await _validator.ValidateAsync(request);
        Assert.True(result.IsValid);
    }
}
