using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    [Fact]
    public void ChecklistOffersOnlyHostCompatiblePluginsAndKeepsMissingIdsChecked()
    {
        using var fixture = new PluginChecklistFixture();
        fixture.WriteManifest("plugin.headless", hosts: ["headless"]);
        fixture.WriteManifest("plugin.graphical", hosts: ["graphical"]);

        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE",
                            "testaccount",
                            "+Acdream",
                            "0x5000000A",
                            LaunchMode.Headless,
                            ["plugin.headless", "ghost.missing"],
                            [],
                            HasRunningSession: false,
                            SessionStatus: "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator, pluginInventory: fixture.Inventory);
        SelectCharacter(viewModel);

        Assert.Equal(2, viewModel.CharacterPluginChoices.Count);
        CharacterPluginChoiceViewModel installed = Assert.Single(
            viewModel.CharacterPluginChoices, choice => choice.Id == "plugin.headless");
        Assert.False(installed.IsMissing);
        Assert.True(installed.IsChecked);
        CharacterPluginChoiceViewModel missing = Assert.Single(
            viewModel.CharacterPluginChoices, choice => choice.Id == "ghost.missing");
        Assert.True(missing.IsMissing);
        Assert.True(missing.IsChecked);
        Assert.DoesNotContain(
            viewModel.CharacterPluginChoices, choice => choice.Id == "plugin.graphical");

        missing.IsChecked = false;
        viewModel.SaveCharacterSettingsCommand.Execute(null);

        Assert.NotNull(orchestrator.SettingsUpdate);
        Assert.Equal(["plugin.headless"], orchestrator.SettingsUpdate.Value.Plugins);
    }

    [Fact]
    public void EachCharactersChecklistIsFilteredByItsOwnLaunchMode()
    {
        using var fixture = new PluginChecklistFixture();
        fixture.WriteManifest("plugin.headless", hosts: ["headless"]);
        fixture.WriteManifest("plugin.graphical", hosts: ["graphical"]);

        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Headless", "0x50000001",
                            LaunchMode.Headless, [], [], false, "Ready"),
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Graphical", "0x50000002",
                            LaunchMode.Gui, [], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator, pluginInventory: fixture.Inventory);

        viewModel.SelectedNode = viewModel.Servers[0].Children[0].Children[0];
        Assert.Equal(
            "plugin.headless",
            Assert.Single(viewModel.CharacterPluginChoices).Id);

        viewModel.SelectedNode = viewModel.Servers[0].Children[0].Children[1];
        Assert.Equal(
            "plugin.graphical",
            Assert.Single(viewModel.CharacterPluginChoices).Id);
    }

    private sealed class PluginChecklistFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-plugin-checklist-tests",
            Guid.NewGuid().ToString("N"));

        public PluginChecklistFixture()
        {
            var paths = new ApplicationPathSet(
                Path.Combine(_root, "config"),
                Path.Combine(_root, "data"),
                Path.Combine(_root, "cache"),
                null);
            Inventory = new PluginInventory(paths, InstalledPluginRecordStore.ForApplicationPaths(paths));
            PluginsDirectory = paths.PluginsDirectory;
        }

        public PluginInventory Inventory { get; }

        private string PluginsDirectory { get; }

        public void WriteManifest(string id, IReadOnlyList<string> hosts)
        {
            string directory = Path.Combine(PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            string hostsJson = string.Join(", ", hosts.Select(host => $"\"{host}\""));
            File.WriteAllText(
                Path.Combine(directory, "plugin.json"),
                $$"""
                {
                  "id": "{{id}}",
                  "displayName": "{{id}}",
                  "version": "0.1.0",
                  "entryDll": "{{id}}.dll",
                  "apiVersion": 1,
                  "hosts": [{{hostsJson}}]
                }
                """);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
