using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModularMonolith.Contracts;
using ModularMonolith.Generators;
using ModularMonolith.Worker.Hosting;

namespace ModularMonolith.Tests;

public sealed class WorkerSourceGeneratorTests
{
    /// <summary>Verifies stable Host source for shared module and client metadata.</summary>
    [Fact]
    public void HostGeneration_IsDeterministicAndContainsSharedMetadata()
    {
        var compilation = Compilation(
            "ModularMonolith.Host",
            "namespace ModularMonolith.Host.Modules; internal interface IHostModuleCustomization { }");

        var first = Run(compilation);
        var second = Run(compilation);
        var source = Assert.Single(first.Results).GeneratedSources.Single().SourceText.ToString();
        var withCustomization = Run(Compilation(
            "ModularMonolith.Host",
            """
            namespace ModularMonolith.Host.Modules;

            internal sealed class ImageCacheHostCustomization
            {
                public string ModuleName => "image-cache";
                public System.Collections.Generic.IEnumerable<string> GetWorkerArguments(
                    Microsoft.Extensions.Configuration.IConfiguration configuration) => [];
            }
            """));
        var sourceWithCustomization = Assert.Single(withCustomization.Results)
            .GeneratedSources
            .Single()
            .SourceText
            .ToString();

        Assert.Equal(source, Assert.Single(second.Results).GeneratedSources.Single().SourceText.ToString());
        Assert.Equal(source, sourceWithCustomization);
        Assert.Contains("AddGeneratedWorkerModules", source);
        Assert.Contains("\"svg-renderer\"", source);
        Assert.Contains("\"/render/{**path}\"", source);
        Assert.Contains("class RendererClient", source);
        Assert.Contains("ContractJsonContext.Default.RenderPageRequest", source);
        Assert.DoesNotContain("System.Reflection", source);
        Assert.DoesNotContain("ImageCacheHostCustomization", source);
    }

    /// <summary>Verifies that a worker module produces one generated entry point.</summary>
    [Fact]
    public void WorkerGeneration_CreatesMainForTheOnlyWorkerModule()
    {
        var compilation = Compilation(
            "Example.Worker",
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;

            namespace Example;

            internal sealed class ExampleWorker : ModularMonolith.Worker.Hosting.IWorkerModule
            {
                public string Name => "example";
                public Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken)
                    => Task.CompletedTask;
                public void MapEndpoints(WebApplication app) { }
            }
            """);

        var run = Run(compilation);
        var source = Assert.Single(Assert.Single(run.Results).GeneratedSources).SourceText.ToString();

        Assert.Contains("WorkerHost.RunAsync", source);
        Assert.Contains("new global::Example.ExampleWorker()", source);
    }

    /// <summary>Verifies the diagnostic for ambiguous worker module discovery.</summary>
    [Fact]
    public void WorkerGeneration_ReportsMultipleModules()
    {
        var compilation = Compilation(
            "Example.Worker",
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;

            namespace Example;

            internal sealed class Alpha : ModularMonolith.Worker.Hosting.IWorkerModule
            {
                public string Name => "alpha";
                public Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken) => Task.CompletedTask;
                public void MapEndpoints(WebApplication app) { }
            }

            internal sealed class Beta : ModularMonolith.Worker.Hosting.IWorkerModule
            {
                public string Name => "beta";
                public Task ConfigureServicesAsync(WebApplicationBuilder builder, CancellationToken cancellationToken) => Task.CompletedTask;
                public void MapEndpoints(WebApplication app) { }
            }
            """);

        var diagnostic = Assert.Single(Run(compilation).Diagnostics);

        Assert.Equal("MMG001", diagnostic.Id);
    }

    /// <summary>Verifies the diagnostic for a worker project without a module.</summary>
    [Fact]
    public void WorkerGeneration_ReportsMissingModule()
    {
        var diagnostic = Assert.Single(Run(Compilation("Example.Worker", string.Empty)).Diagnostics);

        Assert.Equal("MMG008", diagnostic.Id);
    }

    private static GeneratorDriverRunResult Run(CSharpCompilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new WorkerSourceGenerator().AsSourceGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    private static CSharpCompilation Compilation(string assemblyName, string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(
            [
                typeof(SvgRendererModuleDeclaration).Assembly.Location,
                typeof(IWorkerModule).Assembly.Location
            ])
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
