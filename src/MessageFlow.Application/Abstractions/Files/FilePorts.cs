namespace MessageFlow.Application.Abstractions.Files;

/// <summary>Lectura en streaming de un libro Excel (.xlsx). Implementado con MiniExcel en Infrastructure.</summary>
public interface IExcelReader : IDisposable
{
    /// <summary>Lee la primera fila como encabezados, en orden de columna. Lanza si el archivo está vacío.</summary>
    IReadOnlyList<string?> ReadHeaders();

    /// <summary>Itera las filas de datos restantes sin cargar el archivo completo en memoria.</summary>
    IEnumerable<IReadOnlyList<string?>> ReadDataRows();
}

public interface IExcelReaderFactory
{
    /// <summary>Crea un lector sobre el stream. Debe ser un stream legible y buscable (seekable).</summary>
    IExcelReader Open(Stream stream);
}

/// <summary>
/// Almacén del binario original del dataset (referencia trazable en Dataset.StoragePath).
/// El contrato es agnóstico al proveedor: disco local hoy; blob storage en el futuro.
/// </summary>
public interface IDatasetFileStorage
{
    /// <summary>Persiste el contenido del stream y devuelve la ruta relativa asignada.</summary>
    Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken cancellationToken = default);

    /// <summary>Abre el archivo persistido para lectura (stream buscable).</summary>
    Stream OpenRead(string storagePath);

    /// <summary>Elimina el archivo persistido (limpieza ante importaciones fallidas).</summary>
    void Delete(string storagePath);
}
