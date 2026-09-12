namespace MessageFlow.Application.Datasets;

/// <summary>
/// Límites de la importación de datasets. Registrados con valores por defecto;
/// la API puede re-enlazarlos desde configuración sin tocar esta capa.
/// </summary>
public sealed class DatasetImportOptions
{
    /// <summary>Tamaño máximo del archivo original (25 MB por defecto).</summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Filas muestreadas para inferir el tipo de cada columna.</summary>
    public int TypeInferenceSampleSize { get; set; } = 100;

    /// <summary>Filas por lote de inserción (control de memoria en archivos grandes).</summary>
    public int InsertBatchSize { get; set; } = 2000;

    /// <summary>Carpeta raíz (relativa a la app) para el binario original de los datasets.</summary>
    public string StorageRootPath { get; set; } = "data/uploads";

    public static DatasetImportOptions Default { get; } = new();
}
