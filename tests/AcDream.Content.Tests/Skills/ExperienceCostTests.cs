using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Content.Skills;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;

namespace AcDream.Content.Tests.Skills;

public sealed class ExperienceCostTests
{
    // The character sheet's own fixture curve, so a pinned number here proves
    // the moved arithmetic still agrees with CharacterSheetProviderTests.
    private static readonly uint[] AttributeCurve = [0, 10, 30, 60, 100];

    [Fact]
    public void ToRaise_MatchesTheCharacterSheetsFixtureNumbers()
    {
        Assert.Equal(20L, ExperienceCost.ToRaise(AttributeCurve, ranks: 1, spentXp: 10, amount: 1));
        Assert.Equal(90L, ExperienceCost.ToRaise(AttributeCurve, ranks: 1, spentXp: 10, amount: 10));
    }

    [Fact]
    public void ToRaise_AtTheTopOfTheCurveIsZero()
    {
        Assert.Equal(
            0L,
            ExperienceCost.ToRaise(
                AttributeCurve, ranks: (uint)(AttributeCurve.Length - 1), spentXp: 100, amount: 1));
    }

    [Fact]
    public void ToRaise_ClampsAnAmountThatWouldPassTheTopOfTheCurve()
    {
        // Ranks 3, asking for 5 more would land on index 8; the curve only
        // reaches index 4, so the price is for the top rank instead.
        Assert.Equal(40L, ExperienceCost.ToRaise(AttributeCurve, ranks: 3, spentXp: 60, amount: 5));
    }

    [Fact]
    public void ToRaise_WhenSpentAlreadyExceedsTheTargetReturnsZeroRatherThanNegative()
    {
        Assert.Equal(0L, ExperienceCost.ToRaise(AttributeCurve, ranks: 1, spentXp: 1_000, amount: 1));
    }

    [Fact]
    public void ToRaise_WithNoCurveReturnsZero()
    {
        Assert.Equal(0L, ExperienceCost.ToRaise(null, ranks: 1, spentXp: 0, amount: 1));
    }

    [Fact]
    public void LoadTable_ReadsTheKnownIdDirectly()
    {
        var table = new ExperienceTable();
        var dats = new FakeDats();
        dats.Add(0x0E000018u, table);

        Assert.Same(table, ExperienceCost.LoadTable(dats));
    }

    [Fact]
    public void LoadTable_FallsBackToATypeScanWhenTheKnownIdHasNothing()
    {
        var table = new ExperienceTable();
        var dats = new FakeDats();
        dats.Add(0x9999u, table);
        dats.ScanIds.Add(0x9999u);

        Assert.Same(table, ExperienceCost.LoadTable(dats));
    }

    [Fact]
    public void LoadTable_AThrowingDirectReadFallsBackToTheScanAndLogsOnce()
    {
        var table = new ExperienceTable();
        var dats = new FakeDats(throwOnKnownId: true);
        dats.Add(0x9999u, table);
        dats.ScanIds.Add(0x9999u);
        var warnings = new List<string>();

        ExperienceTable? result = ExperienceCost.LoadTable(dats, warnings.Add);

        Assert.Same(table, result);
        string warning = Assert.Single(warnings);
        Assert.Contains("0x0E000018", warning);
    }

    [Fact]
    public void LoadTable_AThrowingScanReturnsNullAndLogs()
    {
        var dats = new FakeDats(throwOnScan: true);
        var warnings = new List<string>();

        ExperienceTable? result = ExperienceCost.LoadTable(dats, warnings.Add);

        Assert.Null(result);
        string warning = Assert.Single(warnings);
        Assert.Contains("scan", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadTable_WithNothingFoundReturnsNull()
    {
        var dats = new FakeDats();
        Assert.Null(ExperienceCost.LoadTable(dats));
    }

    /// <summary>Installed files carrying only whatever <see cref="Add"/> puts in them.</summary>
    private sealed class FakeDats(
        bool throwOnKnownId = false, bool throwOnScan = false) : IDatReaderWriter
    {
        private readonly Dictionary<uint, ExperienceTable> _tables = new();

        internal List<uint> ScanIds { get; } = [];

        public void Add(uint id, ExperienceTable table) => _tables[id] = table;

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj
        {
            if (throwOnScan) throw new InvalidOperationException("scan boom");
            return typeof(T) == typeof(ExperienceTable) ? ScanIds : [];
        }

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj
        {
            if (typeof(T) != typeof(ExperienceTable))
                return default;
            if (throwOnKnownId && fileId == 0x0E000018u)
                throw new InvalidOperationException("direct read boom");
            return _tables.TryGetValue(fileId, out ExperienceTable? table)
                ? (T)(object)table
                : default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }
    }
}
