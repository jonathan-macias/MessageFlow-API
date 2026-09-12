using MessageFlow.Application.Abstractions.Files;
using MessageFlow.Application.Datasets;
using MessageFlow.Application.Datasets.Commands;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Infrastructure.Persistence.Files;
using MessageFlow.Tests.TestSupport;
using MiniExcelLibs;

namespace MessageFlow.Tests;

/// <summary>
/// Carga de Excel con columna telefónica (§7): selección obligatoria, validación contra
/// los encabezados reales del archivo y limpieza del binario cuando la importación falla.
/// </summary>
public class UploadDatasetPhoneColumnTests
{
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeDatasetRepository _datasets = new();
    private readonly InMemoryFileStorage _storage = new();

    private UploadDatasetCommandHandler BuildHandler()
        => new(
            new MiniExcelReaderFactory(),
            _storage,
            _datasets,
            _unitOfWork,
            DatasetImportOptions.Default,
            new FakeCurrentUser());

    /// <summary>Libro físico: primera fila = encabezados, siguientes = datos.</summary>
    private static MemoryStream CreateWorkbook()
    {
        var entries = new List<Dictionary<string, object?>>
        {
            new() { ["Nombre"] = "Carlos", ["Numero Celular"] = "3001234567", ["Numero Auxiliar"] = "3109876543" },
            new() { ["Nombre"] = "Ana", ["Numero Celular"] = "3011112222", ["Numero Auxiliar"] = DBNull.Value },
        };

        var stream = new MemoryStream();
        stream.SaveAs(entries); // las claves se escriben como fila de encabezados
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task La_carga_exitosa_persiste_la_columna_telefonica_seleccionada()
    {
        using var content = CreateWorkbook();

        var datasetId = await BuildHandler().HandleAsync(new UploadDatasetCommand(
            "Contactos",
            "contactos.xlsx",
            content.Length,
            content,
            "Numero Celular"));

        var saved = _datasets.Store[datasetId];
        Assert.Equal("Numero Celular", saved.PhoneColumn);
        Assert.Equal(3, saved.Columns.Count);
        Assert.True(saved.HasColumn("Nombre"));
        Assert.Empty(_storage.Deleted); // nada que limpiar
    }

    [Fact]
    public async Task La_seleccion_es_insensible_a_mayusculas_y_espacios()
    {
        using var content = CreateWorkbook();

        var datasetId = await BuildHandler().HandleAsync(new UploadDatasetCommand(
            "Contactos",
            "contactos.xlsx",
            content.Length,
            content,
            "  numero   celular  "));

        Assert.Equal("Numero Celular", _datasets.Store[datasetId].PhoneColumn);
    }

    [Fact]
    public async Task Una_phoneColumn_que_no_existe_en_el_excel_rechaza_la_carga_y_limpia_el_archivo()
    {
        using var content = CreateWorkbook();

        await Assert.ThrowsAsync<UnknownColumnException>(
            () => BuildHandler().HandleAsync(new UploadDatasetCommand(
                "Contactos",
                "contactos.xlsx",
                content.Length,
                content,
                "Telefono Personal"))); // no está entre los encabezados

        Assert.Empty(_datasets.Store); // no se creó el dataset
        Assert.Single(_storage.Deleted); // el binario huérfano fue eliminado
    }

    /// <summary>Almacén en memoria con registro de eliminaciones para verificar limpieza.</summary>
    private sealed class InMemoryFileStorage : IDatasetFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];
        public List<string> Deleted { get; } = [];

        public Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var path = $"mem/{Guid.NewGuid():N}_{originalFileName}";
            _files[path] = buffer.ToArray();
            return Task.FromResult(path);
        }

        public Stream OpenRead(string storagePath)
            => new MemoryStream(_files.TryGetValue(storagePath, out var bytes) ? bytes : throw new FileNotFoundException(storagePath));

        public void Delete(string storagePath)
        {
            _files.Remove(storagePath);
            Deleted.Add(storagePath);
        }
    }
}
