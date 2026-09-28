using DatReaderWriter.DBObjs;

namespace AcDream.Content.Skills;

/// <summary>
/// Prices a raise from an experience curve and loads the installed table one
/// comes from, shared by the character sheet and the plugin automation
/// surface.
/// </summary>
public static class ExperienceCost
{
    /// <summary>The experience to raise <paramref name="amount"/> ranks from <paramref name="ranks"/>/<paramref name="spentXp"/>, floored at 0.</summary>
    public static long ToRaise(uint[]? curve, uint ranks, uint spentXp, int amount)
    {
        if (curve is null || amount <= 0) return 0L;
        long maxIndex = curve.Length - 1L;
        if (maxIndex <= ranks) return 0L;
        long targetLong = Math.Min((long)ranks + amount, maxIndex);
        long targetXp = curve[(int)targetLong];
        long cost = targetXp - spentXp;
        return cost > 0 ? cost : 0L;
    }

    /// <summary>The installed experience table, by its id or else by type scan; null when neither read succeeds.</summary>
    public static ExperienceTable? LoadTable(
        IDatReaderWriter dats, Action<string>? log = null)
    {
        if (dats is null) return null;

        try
        {
            var table = dats.Get<ExperienceTable>(0x0E000018u);
            if (table is not null) return table;
        }
        catch (Exception ex)
        {
            log?.Invoke($"ExperienceTable 0x0E000018 read failed ({ex.GetType().Name}: {ex.Message}); trying type scan.");
        }

        try
        {
            foreach (uint id in dats.GetAllIdsOfType<ExperienceTable>())
            {
                var table = dats.Get<ExperienceTable>(id);
                if (table is not null) return table;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"ExperienceTable type scan failed ({ex.GetType().Name}: {ex.Message}); raise costs unavailable.");
        }

        return null;
    }
}
