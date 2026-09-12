using MessageFlow.Application.Abstractions.Files;
using MessageFlow.Application.Datasets;

namespace MessageFlow.Infrastructure.Persistence.Files;

/// <summary>
/// Almacenamiento en disco local del binario original de los datasets.
/// Ruta raíz relativa a la aplicación (Docker-friendly: montar volumen en esa ruta).
/// </summary>
public sealed class LocalDiskDatasetFileStorage(DatasetImportOptions options) : IDatasetFileStorage
{
    private string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.StorageRootPath));

    public async Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        Directory.CreateDirectory(Root);

        var extension = Path.GetExtension(originalFileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(Root, fileName);

        await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(target, cancellationToken);

        return $"{options.StorageRootPath.Replace('\\', '/').TrimEnd('/')}/{fileName}";
    }

    public Stream OpenRead(string storagePath)
    {
        var fullPath = Resolve(storagePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Delete(string storagePath)
    {
        var fullPath = Resolve(storagePath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>Resuelve la ruta relativa impidiendo escapes fuera de la raíz (path traversal).</summary>
    private string Resolve(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("La ruta de almacenamiento es obligatoria.", nameof(storagePath));
        }

        var normalized = storagePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);
        var root = Root;
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName));

        if (!fullPath.StartsWith(root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("La ruta de almacenamiento solicitada no es válida.");
        }

        return fullPath;
    }
}
