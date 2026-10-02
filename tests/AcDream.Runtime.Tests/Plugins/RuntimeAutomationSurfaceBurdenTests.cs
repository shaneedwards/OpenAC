using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// What the character carries and can carry, read the way the pack's own
/// readout reads it so a plugin planning a Strength raise sees the same
/// numbers the player does.
/// </summary>
public sealed class RuntimeAutomationSurfaceBurdenTests
{
    private const uint Player = 0x50000001u;
    private const uint StrengthId = 1u;

    [Fact]
    public void LoadIsTheServersTotalAndCapacityFollowsStrengthAndTheAugmentation()
    {
        using Fixture fixture = Fixture.InWorld(strength: 100, aug: 3, wireLoad: 17250);
        ICharacterInfo character = fixture.Surface;

        Assert.Equal(17250, character.BurdenLoad);
        Assert.Equal(24000, character.BurdenCapacity);
    }

    [Fact]
    public void LoadIsTheSumOfWhatIsCarriedWithoutAServerTotal()
    {
        using Fixture fixture = Fixture.InWorld(strength: 100, aug: 0, wireLoad: null);

        Assert.Equal(300, fixture.Character.BurdenLoad);
        Assert.Equal(15000, fixture.Character.BurdenCapacity);
    }

    [Fact]
    public void CapacityIsReadAtTheBuffedStrengthAndFallsWhenTheBuffLapses()
    {
        using Fixture fixture = Fixture.InWorld(strength: 100, aug: 0, wireLoad: null);
        var book = fixture.Runtime.CharacterOwner.Spellbook;
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 77u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: StrengthId, StatModValue: 1.5f, Bucket: 1u));

        Assert.Equal(22500, fixture.Character.BurdenCapacity);

        book.OnEnchantmentRemoved(1u, 77u);

        Assert.Equal(15000, fixture.Character.BurdenCapacity);
    }

    [Fact]
    public void AnUnboundSurfaceHasNoBurden()
    {
        using var surface = new RuntimeAutomationSurface();
        ICharacterInfo character = surface;

        Assert.Equal(0, character.BurdenLoad);
        Assert.Equal(0, character.BurdenCapacity);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly NoWindowGameRuntimeHost _host;

        private Fixture(NoWindowGameRuntimeHost host, RuntimeAutomationSurface surface)
        {
            _host = host;
            Surface = surface;
        }

        internal RuntimeAutomationSurface Surface { get; }

        internal ICharacterInfo Character => Surface;

        internal GameRuntime Runtime => _host.Runtime;

        internal static Fixture InWorld(int strength, int aug, int? wireLoad)
        {
            var host = new NoWindowGameRuntimeHost();
            host.Start();
            for (int tick = 0; tick < 4; tick++)
                host.Session.Tick();
            var runtime = host.Runtime;
            Assert.True(runtime.Session.IsInWorld);

            runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create([BuffSpell()]));
            runtime.CharacterOwner.LocalPlayer.OnAttributeUpdate(
                atType: StrengthId, ranks: 0u, start: (uint)strength, xp: 0u);

            var player = new ClientObject { ObjectId = Player };
            player.Properties.Ints[(uint)PropertyInt.AugmentationIncreasedCarryingCapacity] = aug;
            if (wireLoad is int load)
                player.Properties.Ints[(uint)PropertyInt.EncumbranceVal] = load;
            ClientObjectTable objects = runtime.InventoryOwner.Objects;
            objects.AddOrUpdate(player);
            objects.AddOrUpdate(new ClientObject { ObjectId = 0xA, ContainerId = Player, Burden = 100 });
            objects.AddOrUpdate(new ClientObject { ObjectId = 0xD, WielderId = Player, Burden = 200 });

            var surface = new RuntimeAutomationSurface();
            surface.Bind(
                runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
            Assert.True(surface.IsAvailable);
            return new Fixture(host, surface);
        }

        public void Dispose()
        {
            Surface.Dispose();
            _host.Dispose();
        }

        private static SpellMetadata BuffSpell() => new(
            77u, "Strength Self", "Creature Enchantment", 7u, 0u, string.Empty, 60f, 10,
            true, false, string.Empty, 0, 350, 0u, 7, false, true, false,
            0f, 0u, 0u, 1u, 0);
    }
}
