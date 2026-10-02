using AcDream.Core.Items;
using AcDream.Core.Properties;
using Xunit;

namespace AcDream.Core.Tests.Items;

public class BurdenReadTests
{
    private const uint Player = 0x50000001u;

    private static ClientObjectTable TableWith(int? wireLoad, int? aug)
    {
        var t = new ClientObjectTable();
        var player = new ClientObject { ObjectId = Player };
        if (wireLoad is int load)
            player.Properties.Ints[(uint)PropertyInt.EncumbranceVal] = load;
        if (aug is int rank)
            player.Properties.Ints[(uint)PropertyInt.AugmentationIncreasedCarryingCapacity] = rank;
        t.AddOrUpdate(player);
        t.AddOrUpdate(new ClientObject { ObjectId = 0xA, ContainerId = Player, Burden = 100 });
        t.AddOrUpdate(new ClientObject { ObjectId = 0xD, WielderId = Player, Burden = 200 });
        return t;
    }

    [Fact]
    public void Load_is_the_servers_total_when_it_sent_one()
        => Assert.Equal(7777, BurdenMath.Read(TableWith(7777, null), Player, 100).Load);

    [Fact]
    public void Load_is_the_sum_of_what_is_carried_without_a_server_total()
        => Assert.Equal(300, BurdenMath.Read(TableWith(null, null), Player, 100).Load);

    [Fact]
    public void A_server_total_of_zero_is_trusted_over_the_sum()
        => Assert.Equal(0, BurdenMath.Read(TableWith(0, null), Player, 100).Load);

    [Theory]
    [InlineData(0, 15000)]
    [InlineData(3, 24000)]
    [InlineData(10, 30000)]
    public void Capacity_follows_strength_and_the_augmentation_up_to_its_cap(int aug, int expected)
        => Assert.Equal(expected, BurdenMath.Read(TableWith(null, aug), Player, 100).Capacity);

    [Fact]
    public void Capacity_follows_the_strength_it_is_given()
    {
        ClientObjectTable t = TableWith(null, 0);
        Assert.Equal(15000, BurdenMath.Read(t, Player, 100).Capacity);
        Assert.Equal(22500, BurdenMath.Read(t, Player, 150).Capacity);
    }

    [Fact]
    public void An_unknown_player_carries_nothing()
        => Assert.Equal((0, 15000), BurdenMath.Read(new ClientObjectTable(), Player, 100));

    [Theory]
    [InlineData(1000, 15000, 6)]
    [InlineData(15000, 15000, 100)]
    [InlineData(22500, 15000, 150)]
    [InlineData(99999, 15000, 300)]
    public void The_packs_percent_is_unchanged_through_the_shared_read(
        int wireLoad, int capacity, int percent)
    {
        (int load, int cap) = BurdenMath.Read(TableWith(wireLoad, null), Player, 100);

        Assert.Equal(capacity, cap);
        Assert.Equal(percent, BurdenMath.LoadToPercent(BurdenMath.LoadRatio(cap, load)));
    }
}
