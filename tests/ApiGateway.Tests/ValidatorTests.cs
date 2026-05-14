using ApiGateway.Validators;
using Microsoft.AspNetCore.Http;
using Moq;
using Shared.Contracts;

namespace ApiGateway.Tests;

public class ValidatorTests
{
    private readonly UploadDocumentRequestValidator _validator = new();

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private static Mock<IFormFile> BuildFileMock(string contentType, long length) =>
        new Mock<IFormFile>()
            .Also(m =>
            {
                m.Setup(f => f.ContentType).Returns(contentType);
                m.Setup(f => f.Length).Returns(length);
            });

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Validate_WhenFileIsNull_FailsValidation()
    {
        // Arrange
        var request = new UploadDocumentRequest { File = null! };

        // Act
        var result = await _validator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_WhenFileIsEmpty_FailsValidation()
    {
        // Arrange
        var emptyFile = BuildFileMock(contentType: "application/pdf", length: 0);
        var request = new UploadDocumentRequest { File = emptyFile.Object };

        // Act
        var result = await _validator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_WhenFileIsNotPdf_FailsValidation()
    {
        // Arrange
        var imageFile = BuildFileMock(contentType: "image/jpeg", length: 1024);
        var request = new UploadDocumentRequest { File = imageFile.Object };

        // Act
        var result = await _validator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_WhenFileIsValidPdf_PassesValidation()
    {
        // Arrange
        var validPdf = BuildFileMock(contentType: "application/pdf", length: 1024);
        var request = new UploadDocumentRequest { File = validPdf.Object };

        // Act
        var result = await _validator.ValidateAsync(request);

        // Assert
        Assert.True(result.IsValid);
    }
}

file static class MockExtensions
{
    public static Mock<T> Also<T>(this Mock<T> mock, Action<Mock<T>> setup) where T : class
    {
        setup(mock);
        return mock;
    }
}
