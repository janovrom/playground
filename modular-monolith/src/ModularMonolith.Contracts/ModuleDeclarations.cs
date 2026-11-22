namespace ModularMonolith.Contracts;

/// <summary>Declares one worker module and its Host registration metadata.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WorkerModuleAttribute : Attribute
{
    /// <summary>Creates stable identity and Host registration metadata for a worker module.</summary>
    public WorkerModuleAttribute(string name, string workerProject, string configSection)
    {
        Name = name;
        WorkerProject = workerProject;
        ConfigSection = configSection;
    }

    /// <summary>Gets the stable module name.</summary>
    public string Name { get; }

    /// <summary>Gets the worker project name used to resolve its executable.</summary>
    public string WorkerProject { get; }

    /// <summary>Gets the configuration section used for worker options.</summary>
    public string ConfigSection { get; }
}

/// <summary>Declares a public route that the Host forwards to a worker.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WorkerProxyRouteAttribute : Attribute
{
    /// <summary>Creates one public-to-worker path transform.</summary>
    public WorkerProxyRouteAttribute(string publicPath, string destinationPath)
    {
        PublicPath = publicPath;
        DestinationPath = destinationPath;
    }

    /// <summary>Gets the public route pattern matched by the Host gateway.</summary>
    public string PublicPath { get; }

    /// <summary>Gets the path template sent to the worker.</summary>
    public string DestinationPath { get; }
}

/// <summary>Declares the worker client generated for a module declaration.</summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class WorkerClientAttribute : Attribute
{
    /// <summary>Creates client metadata for the named worker module.</summary>
    public WorkerClientAttribute(string moduleName) => ModuleName = moduleName;

    /// <summary>Gets the module name used to resolve the declaration and client configuration.</summary>
    public string ModuleName { get; }
}

/// <summary>Identifies how a worker client serializes an operation request.</summary>
public enum WorkerRequestKind
{
    None,
    Json,
    Bytes
}

/// <summary>Identifies how a worker client reads an operation response.</summary>
public enum WorkerResponseKind
{
    None,
    Json,
    Text,
    Bytes
}

/// <summary>Declares the HTTP shape of one generated worker client operation.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class WorkerClientOperationAttribute : Attribute
{
    /// <summary>Creates operation metadata for generated HTTP dispatch.</summary>
    public WorkerClientOperationAttribute(
        string httpMethod,
        string path,
        WorkerRequestKind requestKind,
        WorkerResponseKind responseKind)
    {
        HttpMethod = httpMethod;
        Path = path;
        RequestKind = requestKind;
        ResponseKind = responseKind;
    }

    /// <summary>Gets the HTTP method used by the operation.</summary>
    public string HttpMethod { get; }

    /// <summary>Gets the relative path template used by the operation.</summary>
    public string Path { get; }

    /// <summary>Gets the request body representation.</summary>
    public WorkerRequestKind RequestKind { get; }

    /// <summary>Gets the response body representation.</summary>
    public WorkerResponseKind ResponseKind { get; }

    /// <summary>Gets or sets the content type used for a byte request.</summary>
    public string? RequestContentType { get; set; }

    /// <summary>Gets or sets whether a 404 response maps to a nullable result.</summary>
    public bool TreatNotFoundAsNull { get; set; }
}

/// <summary>Declares the SvgRenderer worker module.</summary>
[WorkerModule(ModuleCatalog.SvgRenderer, "ModularMonolith.Worker.SvgRenderer", "SvgRenderer")]
[WorkerProxyRoute("/render/{**path}", "/render/{**path}")]
public sealed class SvgRendererModuleDeclaration;

/// <summary>Declares the ImageCache worker module.</summary>
[WorkerModule(ModuleCatalog.ImageCache, "ModularMonolith.Worker.ImageCache", "ImageCache")]
[WorkerProxyRoute("/images/{**path}", "/images/{**path}")]
public sealed class ImageCacheModuleDeclaration;
