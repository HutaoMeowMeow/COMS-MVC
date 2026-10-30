using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace COMS.Tests;

/// <summary>Minimal IWebHostEnvironment stand-in for controller tests.</summary>
public sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string WebRootPath { get; set; } = "wwwroot";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; } = "COMS";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = ".";
    /// <summary>Defaults to Production like real hosting outside of Development.</summary>
    public string EnvironmentName { get; set; } = "Production";
}
