using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace TerrariaMissionBridge;

[ApiVersion(2, 1)]
public sealed class TerrariaMissionBridgePlugin : TerrariaPlugin
{
    private const string PluginPermissionAdmin = "terrariamissionbridge.admin";

    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private readonly object _sync = new object();

    private BridgeConfig _config = new BridgeConfig();

    private readonly Dictionary<string, BridgeProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, List<MissionEntry>> _missionsByItemId = new();

    private readonly List<MissionGroup> _missionGroups = new();

    private readonly Dictionary<string, List<GroupRequirement>> _requirementsByMissionId = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _players = new(StringComparer.OrdinalIgnoreCase);

    private string PluginDirectory => Path.Combine(TShock.SavePath, "TerrariaMissionBridge");
    private string ConfigPath => Path.Combine(PluginDirectory, "config.txt");
    private string ProfilesPath => Path.Combine(PluginDirectory, "profiles.txt");
    private string MissionsPath => Path.Combine(PluginDirectory, "missions.txt");
    private string MissionGroupsPath => Path.Combine(PluginDirectory, "mission_groups.txt");
    private string MissionRequirementsPath => Path.Combine(PluginDirectory, "mission_requirements.txt");
    private string PlayersPath => Path.Combine(PluginDirectory, "players.txt");

    public override string Name => "TerrariaMissionBridge";
    public override string Author => "Rumic Bot / OpenAI";
    public override string Description => "Conecta entregas de items de Terraria con misiones de un bot de Discord.";
    public override Version Version => new Version(1, 3, 0);

    public TerrariaMissionBridgePlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        EnsureFiles();
        ReloadFiles();

        Commands.ChatCommands.Add(new Command(DiscordCommand, "discord")
        {
            HelpText = "Vincula tu personaje con Discord. Uso: /discord <idDiscord>"
        });

        Commands.ChatCommands.Add(new Command(DeliverCommand, "entregar")
        {
            HelpText = "Entrega una misión disponible. Usa el item en mano o revisa misiones múltiples del inventario."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, ItemInfoCommand, "mbitem")
        {
            HelpText = "Muestra el ID, nombre y cantidad del item en tu mano."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, ReloadCommand, "mbreload")
        {
            HelpText = "Recarga los archivos de TerrariaMissionBridge."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, EnableCommand, "mbon")
        {
            HelpText = "Activa el sistema de entregas."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, DisableCommand, "mboff")
        {
            HelpText = "Desactiva temporalmente el sistema de entregas."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, StatusCommand, "mbstatus")
        {
            HelpText = "Muestra el estado actual del sistema de entregas."
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Commands.ChatCommands.RemoveAll(command =>
                command.Names.Contains("discord") ||
                command.Names.Contains("entregar") ||
                command.Names.Contains("mbitem") ||
                command.Names.Contains("mbreload") ||
                command.Names.Contains("mbon") ||
                command.Names.Contains("mboff") ||
                command.Names.Contains("mbstatus"));
        }

        base.Dispose(disposing);
    }

    private void EnsureFiles()
    {
        Directory.CreateDirectory(PluginDirectory);

        if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(
                ConfigPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# Config general del plugin",
                    "",
                    "Enabled = true",
                    "DefaultProfile = main",
                    "ConsumeItemOnSuccess = true"
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(ProfilesPath))
        {
            File.WriteAllText(
                ProfilesPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# profile | endpoint | secret | guildId",
                    "# endpoint debe apuntar a /terraria/mission-complete",
                    "# El plugin calculará /terraria/mission-prepare automáticamente.",
                    "# Ejemplo:",
                    "# main | http://IP_O_DOMINIO_DEL_BOT:PUERTO/terraria/mission-complete | CLAVE_SECRETA | ID_SERVIDOR_DISCORD",
                    "",
                    "main | http://127.0.0.1:3000/terraria/mission-complete | cambia_esta_clave | cambia_este_guild_id"
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(MissionsPath))
        {
            File.WriteAllText(
                MissionsPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# Misiones simples",
                    "# itemId | missionId | amount | profile",
                    "# profile es opcional. Si no lo pones, usa DefaultProfile.",
                    "",
                    "29 | madera_entregada | 10 | main",
                    "75 | espada_hierro_entregada | 1 | main"
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(MissionGroupsPath))
        {
            File.WriteAllText(
                MissionGroupsPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# Misiones múltiples",
                    "# missionId | mode | requiredOptions | profile",
                    "# mode:",
                    "# group_any = requiere cumplir X opciones de todas las configuradas",
                    "# group_all = requiere cumplir todas las opciones",
                    "",
                    "cajas_biomas | group_any | 5 | main"
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(MissionRequirementsPath))
        {
            File.WriteAllText(
                MissionRequirementsPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# Requisitos para misiones múltiples",
                    "# missionId | optionId | itemId | amount | label",
                    "",
                    "cajas_biomas | ocean | 2334 | 2 | Caja costera",
                    "cajas_biomas | frozen | 2335 | 2 | Caja congelada",
                    "cajas_biomas | oasis | 4405 | 2 | Caja oasis",
                    "cajas_biomas | jungle | 2336 | 2 | Caja jungla",
                    "cajas_biomas | corruption | 2337 | 2 | Caja corrupción",
                    "cajas_biomas | crimson | 2338 | 2 | Caja carmesí",
                    "cajas_biomas | hallow | 3203 | 2 | Caja sagrada",
                    "cajas_biomas | dungeon | 3204 | 2 | Caja calabozo",
                    "cajas_biomas | desert | 3205 | 2 | Caja desierto",
                    "cajas_biomas | hell | 3206 | 2 | Caja infierno"
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(PlayersPath))
        {
            File.WriteAllText(
                PlayersPath,
                string.Join(Environment.NewLine, new[]
                {
                    "# playerName | discordUserId",
                    "# Este archivo también se puede llenar con /discord <idDiscord>.",
                    ""
                }) + Environment.NewLine,
                Encoding.UTF8);
        }
    }

    private void ReloadFiles()
    {
        lock (_sync)
        {
            _config = LoadConfig();

            _profiles.Clear();
            _missionsByItemId.Clear();
            _missionGroups.Clear();
            _requirementsByMissionId.Clear();
            _players.Clear();

            foreach (var profile in LoadProfiles())
            {
                _profiles[profile.Name] = profile;
            }

            foreach (var mission in LoadMissions())
            {
                if (!_missionsByItemId.TryGetValue(mission.ItemId, out var list))
                {
                    list = new List<MissionEntry>();
                    _missionsByItemId[mission.ItemId] = list;
                }

                list.Add(mission);
            }

            foreach (var group in LoadMissionGroups())
            {
                _missionGroups.Add(group);
            }

            foreach (var requirement in LoadMissionRequirements())
            {
                if (!_requirementsByMissionId.TryGetValue(requirement.MissionId, out var list))
                {
                    list = new List<GroupRequirement>();
                    _requirementsByMissionId[requirement.MissionId] = list;
                }

                list.Add(requirement);
            }

            foreach (var player in LoadPlayers())
            {
                _players[player.PlayerName] = player.DiscordUserId;
            }
        }
    }

    private BridgeConfig LoadConfig()
    {
        var config = new BridgeConfig();

        foreach (var line in ReadUsefulLines(ConfigPath))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0];
            var value = parts[1];

            if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                config.Enabled = ParseBoolean(value, true);
            }

            if (key.Equals("DefaultProfile", StringComparison.OrdinalIgnoreCase))
            {
                config.DefaultProfile = string.IsNullOrWhiteSpace(value) ? "main" : value.Trim();
            }

            if (key.Equals("ConsumeItemOnSuccess", StringComparison.OrdinalIgnoreCase))
            {
                config.ConsumeItemOnSuccess = ParseBoolean(value, true);
            }
        }

        return config;
    }

    private IEnumerable<BridgeProfile> LoadProfiles()
    {
        foreach (var line in ReadUsefulLines(ProfilesPath))
        {
            var parts = SplitPipe(line);

            if (parts.Length < 4)
            {
                continue;
            }

            var name = parts[0];
            var endpoint = parts[1];
            var secret = parts[2];
            var guildId = parts[3];

            if (
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(secret) ||
                string.IsNullOrWhiteSpace(guildId))
            {
                continue;
            }

            yield return new BridgeProfile
            {
                Name = name,
                Endpoint = endpoint,
                Secret = secret,
                GuildId = guildId
            };
        }
    }

    private IEnumerable<MissionEntry> LoadMissions()
    {
        foreach (var line in ReadUsefulLines(MissionsPath))
        {
            var parts = SplitPipe(line);

            if (parts.Length < 3)
            {
                continue;
            }

            if (!int.TryParse(parts[0], out var itemId))
            {
                continue;
            }

            var missionId = parts[1];

            if (!int.TryParse(parts[2], out var amount))
            {
                amount = 1;
            }

            amount = Math.Max(1, amount);

            var profile = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3])
                ? parts[3]
                : _config.DefaultProfile;

            if (string.IsNullOrWhiteSpace(missionId))
            {
                continue;
            }

            yield return new MissionEntry
            {
                ItemId = itemId,
                MissionId = missionId,
                Amount = amount,
                ProfileName = profile
            };
        }
    }
private IEnumerable<MissionGroup> LoadMissionGroups()
    {
        foreach (var line in ReadUsefulLines(MissionGroupsPath))
        {
            var parts = SplitPipe(line);

            if (parts.Length < 3)
            {
                continue;
            }

            var missionId = parts[0];
            var mode = parts[1].ToLowerInvariant();

            if (!int.TryParse(parts[2], out var requiredOptions))
            {
                requiredOptions = 1;
            }

            var profile = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3])
                ? parts[3]
                : _config.DefaultProfile;

            if (string.IsNullOrWhiteSpace(missionId))
            {
                continue;
            }

            if (mode != "group_any" && mode != "group_all")
            {
                mode = "group_all";
            }

            yield return new MissionGroup
            {
                MissionId = missionId,
                Mode = mode,
                RequiredOptions = Math.Max(1, requiredOptions),
                ProfileName = profile
            };
        }
    }

    private IEnumerable<GroupRequirement> LoadMissionRequirements()
    {
        foreach (var line in ReadUsefulLines(MissionRequirementsPath))
        {
            var parts = SplitPipe(line);

            if (parts.Length < 4)
            {
                continue;
            }

            var missionId = parts[0];
            var optionId = parts[1];

            if (!int.TryParse(parts[2], out var itemId))
            {
                continue;
            }

            if (!int.TryParse(parts[3], out var amount))
            {
                amount = 1;
            }

            var label = parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4])
                ? parts[4]
                : $"Item {itemId}";

            if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(optionId))
            {
                continue;
            }

            yield return new GroupRequirement
            {
                MissionId = missionId,
                OptionId = optionId,
                ItemId = itemId,
                Amount = Math.Max(1, amount),
                Label = label
            };
        }
    }

    private IEnumerable<PlayerLink> LoadPlayers()
    {
        foreach (var line in ReadUsefulLines(PlayersPath))
        {
            var parts = SplitPipe(line);

            if (parts.Length < 2)
            {
                continue;
            }

            var playerName = parts[0];
            var discordUserId = parts[1];

            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(discordUserId))
            {
                continue;
            }

            yield return new PlayerLink
            {
                PlayerName = playerName,
                DiscordUserId = discordUserId
            };
        }
    }

    private IEnumerable<string> ReadUsefulLines(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(path, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("#"));
    }

    private string[] SplitPipe(string line)
    {
        return line
            .Split('|')
            .Select(part => part.Trim())
            .ToArray();
    }

    private bool ParseBoolean(string value, bool fallback)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();

        if (text is "true" or "1" or "yes" or "on" or "si" or "sí")
        {
            return true;
        }

        if (text is "false" or "0" or "no" or "off")
        {
            return false;
        }

        return fallback;
    }

    private void DiscordCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 1)
        {
            player.SendErrorMessage("Uso: /discord <idDiscord>");
            return;
        }

        var discordId = args.Parameters[0].Trim();

        if (!IsValidDiscordId(discordId))
        {
            player.SendErrorMessage("ID de Discord inválida. Debe ser numérica y tener entre 17 y 20 dígitos.");
            return;
        }

        lock (_sync)
        {
            _players[player.Name] = discordId;
            SavePlayers();
        }

        player.SendSuccessMessage($"Tu personaje quedó vinculado al Discord ID: {discordId}");
    }

    private bool IsValidDiscordId(string value)
    {
        return value.Length is >= 17 and <= 20 && value.All(char.IsDigit);
    }

    private void SavePlayers()
    {
        var lines = new List<string>
        {
            "# playerName | discordUserId",
            "# Este archivo también se puede llenar con /discord <idDiscord>.",
            ""
        };

        foreach (var pair in _players.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{pair.Key} | {pair.Value}");
        }

        File.WriteAllLines(PlayersPath, lines, Encoding.UTF8);
    }

    private async void DeliverCommand(CommandArgs args)
    {
        try
        {
            await DeliverCommandAsync(args);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[TerrariaMissionBridge] Error en /entregar: {ex}");
            args.Player?.SendErrorMessage("Ocurrió un error entregando la misión.");
        }
    }

    private async System.Threading.Tasks.Task DeliverCommandAsync(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (!_config.Enabled)
        {
            player.SendErrorMessage("El sistema de entregas está desactivado temporalmente.");
            return;
        }

        string? discordUserId;

        lock (_sync)
        {
            _players.TryGetValue(player.Name, out discordUserId);
        }

        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            player.SendErrorMessage("Primero vincula tu Discord con: /discord <idDiscord>");
            return;
        }

        if (TryFindSimpleMission(player, out var simpleMission, out var simpleProfile))
        {
            await DeliverSimpleMissionAsync(player, discordUserId, simpleMission, simpleProfile);
            return;
        }

        if (TryFindGroupMission(player, out var groupMission, out var groupProfile, out var groupPlan))
        {
            await DeliverGroupMissionAsync(player, discordUserId, groupMission, groupProfile, groupPlan);
            return;
        }

        player.SendErrorMessage("No tienes ningún item o conjunto de items listo para entregar.");
    }

    private bool TryFindSimpleMission(
        TSPlayer player,
        out MissionEntry mission,
        out BridgeProfile profile)
    {
        mission = new MissionEntry();
        profile = new BridgeProfile();

        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type <= 0 || heldItem.stack <= 0)
        {
            return false;
        }

        MissionEntry? foundMission = null;
        BridgeProfile? foundProfile = null;

        lock (_sync)
        {
            if (_missionsByItemId.TryGetValue(heldItem.type, out var missions))
            {
                foundMission = missions.FirstOrDefault(entry => heldItem.stack >= entry.Amount);

                if (foundMission != null)
                {
                    _profiles.TryGetValue(foundMission.ProfileName, out foundProfile);
                }
            }
        }

        if (foundMission == null || foundProfile == null)
        {
            return false;
        }

        mission = foundMission;
        profile = foundProfile;
        return true;
    }

    private bool TryFindGroupMission(
        TSPlayer player,
        out MissionGroup group,
        out BridgeProfile profile,
        out GroupDeliveryPlan plan)
    {
        group = new MissionGroup();
        profile = new BridgeProfile();
        plan = new GroupDeliveryPlan();

        List<MissionGroup> groupsSnapshot;
        Dictionary<string, List<GroupRequirement>> requirementsSnapshot;
        Dictionary<string, BridgeProfile> profilesSnapshot;

        lock (_sync)
        {
            groupsSnapshot = _missionGroups.ToList();
            requirementsSnapshot = _requirementsByMissionId.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
            profilesSnapshot = new Dictionary<string, BridgeProfile>(_profiles, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var currentGroup in groupsSnapshot)
        {
            if (!requirementsSnapshot.TryGetValue(currentGroup.MissionId, out var requirements))
            {
                continue;
            }

            if (!profilesSnapshot.TryGetValue(currentGroup.ProfileName, out var currentProfile))
            {
                continue;
            }

            if (TryBuildGroupDeliveryPlan(player, currentGroup, requirements, out var currentPlan))
            {
                group = currentGroup;
                profile = currentProfile;
                plan = currentPlan;
                return true;
            }
        }

        return false;
    }

    private async System.Threading.Tasks.Task DeliverSimpleMissionAsync(
        TSPlayer player,
        string discordUserId,
        MissionEntry mission,
        BridgeProfile profile)
    {
        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type != mission.ItemId || heldItem.stack < mission.Amount)
        {
            player.SendErrorMessage("Ya no tienes el item requerido en la mano.");
            return;
        }

        player.SendInfoMessage($"Validando misión '{mission.MissionId}' con el bot...");

        var prepareResponse = await SendMissionPrepareAsync(profile, discordUserId, mission.MissionId);

        if (!prepareResponse.Ok)
        {
            player.SendErrorMessage(GetFriendlyBridgeError(prepareResponse));
            return;
        }

        var revalidatedItem = GetHeldItem(player);

        if (
            revalidatedItem == null ||
            revalidatedItem.IsAir ||
            revalidatedItem.type != mission.ItemId ||
            revalidatedItem.stack < mission.Amount)
        {
            player.SendErrorMessage("La misión fue validada, pero ya no tienes el item requerido en la mano. No se completó ni se consumió nada.");
            return;
        }

        var consumedItems = new List<ConsumedItem>();

        if (_config.ConsumeItemOnSuccess)
        {
            var consumedItem = ConsumeHeldItem(player, mission.Amount);

            if (consumedItem == null)
            {
                player.SendErrorMessage("No pude consumir el item requerido. No se completó la misión.");
                return;
            }

            consumedItems.Add(consumedItem);
        }

        player.SendInfoMessage($"Completando misión '{mission.MissionId}' con el bot...");

        var completeResponse = await SendMissionCompleteAsync(profile, discordUserId, mission.MissionId);

        if (!completeResponse.Ok)
        {
            RefundConsumedItems(player, consumedItems);
            player.SendErrorMessage(GetFriendlyBridgeError(completeResponse));
            return;
        }

        player.SendSuccessMessage($"Misión completada: {completeResponse.MissionTitle ?? mission.MissionId}");

        if (!string.IsNullOrWhiteSpace(completeResponse.RewardText))
        {
            player.SendInfoMessage(completeResponse.RewardText);
        }
    }

    private async System.Threading.Tasks.Task DeliverGroupMissionAsync(
        TSPlayer player,
        string discordUserId,
        MissionGroup group,
        BridgeProfile profile,
        GroupDeliveryPlan initialPlan)
    {
        player.SendInfoMessage($"Validando misión múltiple '{group.MissionId}' con el bot...");

        var prepareResponse = await SendMissionPrepareAsync(profile, discordUserId, group.MissionId);

        if (!prepareResponse.Ok)
        {
            player.SendErrorMessage(GetFriendlyBridgeError(prepareResponse));
            return;
        }

        List<GroupRequirement>? requirements;

        lock (_sync)
        {
            _requirementsByMissionId.TryGetValue(group.MissionId, out requirements);
            requirements = requirements?.ToList();
        }

        if (requirements == null || !TryBuildGroupDeliveryPlan(player, group, requirements, out var finalPlan))
        {
            player.SendErrorMessage("La misión fue validada, pero ya no tienes los items requeridos. No se completó ni se consumió nada.");
            return;
        }

        List<ConsumedItem> consumedItems = new List<ConsumedItem>();

        if (_config.ConsumeItemOnSuccess)
        {
            consumedItems = ConsumeGroupPlan(player, finalPlan);

            if (consumedItems.Count <= 0)
            {
                player.SendErrorMessage("No pude consumir los items requeridos. No se completó la misión.");
                return;
            }
        }

        player.SendInfoMessage($"Completando misión múltiple '{group.MissionId}' con el bot...");

        var completeResponse = await SendMissionCompleteAsync(profile, discordUserId, group.MissionId);

        if (!completeResponse.Ok)
        {
            RefundConsumedItems(player, consumedItems);
            player.SendErrorMessage(GetFriendlyBridgeError(completeResponse));
            return;
        }

        player.SendSuccessMessage($"Misión completada: {completeResponse.MissionTitle ?? group.MissionId}");

        if (finalPlan.SelectedRequirements.Count > 0)
        {
            player.SendInfoMessage("Items entregados:");
            foreach (var requirement in finalPlan.SelectedRequirements)
            {
                player.SendInfoMessage($"- {requirement.Amount}x {requirement.Label}");
            }
        }

        if (!string.IsNullOrWhiteSpace(completeResponse.RewardText))
        {
            player.SendInfoMessage(completeResponse.RewardText);
        }
    }
private bool TryBuildGroupDeliveryPlan(
        TSPlayer player,
        MissionGroup group,
        List<GroupRequirement> requirements,
        out GroupDeliveryPlan plan)
    {
        plan = new GroupDeliveryPlan
        {
            MissionId = group.MissionId
        };

        if (requirements.Count <= 0)
        {
            return false;
        }

        var states = BuildInventoryStates(player);
        var selectedRequirements = new List<GroupRequirement>();
        var selectedItems = new List<ConsumedItem>();

        foreach (var requirement in requirements)
        {
            if (TryAllocateRequirement(states, requirement, out var consumedItems))
            {
                selectedRequirements.Add(requirement);
                selectedItems.AddRange(consumedItems);

                if (group.Mode == "group_any" && selectedRequirements.Count >= group.RequiredOptions)
                {
                    break;
                }
            }
        }

        var requiredCount = group.Mode == "group_all"
            ? requirements.Count
            : Math.Min(group.RequiredOptions, requirements.Count);

        if (selectedRequirements.Count < requiredCount)
        {
            return false;
        }

        plan.SelectedRequirements = selectedRequirements;
        plan.ItemsToConsume = selectedItems;
        return true;
    }

    private List<InventorySlotState> BuildInventoryStates(TSPlayer player)
    {
        var states = new List<InventorySlotState>();

        for (var slot = 0; slot < player.TPlayer.inventory.Length; slot++)
        {
            var item = player.TPlayer.inventory[slot];

            if (item == null || item.IsAir || item.type <= 0 || item.stack <= 0)
            {
                continue;
            }

            states.Add(new InventorySlotState
            {
                Slot = slot,
                Type = item.type,
                StackRemaining = item.stack,
                Prefix = item.prefix,
                Name = item.Name
            });
        }

        return states;
    }

    private bool TryAllocateRequirement(
        List<InventorySlotState> states,
        GroupRequirement requirement,
        out List<ConsumedItem> consumedItems)
    {
        consumedItems = new List<ConsumedItem>();

        var available = states
            .Where(state => state.Type == requirement.ItemId && state.StackRemaining > 0)
            .Sum(state => state.StackRemaining);

        if (available < requirement.Amount)
        {
            return false;
        }

        var remaining = requirement.Amount;

        foreach (var state in states.Where(state => state.Type == requirement.ItemId && state.StackRemaining > 0))
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(state.StackRemaining, remaining);

            state.StackRemaining -= take;
            remaining -= take;

            consumedItems.Add(new ConsumedItem
            {
                Slot = state.Slot,
                Type = state.Type,
                Stack = take,
                Prefix = state.Prefix,
                Name = state.Name
            });
        }

        return remaining <= 0;
    }

    private Item? GetHeldItem(TSPlayer player)
    {
        var selectedSlot = player.TPlayer.selectedItem;

        if (selectedSlot < 0 || selectedSlot >= player.TPlayer.inventory.Length)
        {
            return null;
        }

        return player.TPlayer.inventory[selectedSlot];
    }

    private ConsumedItem? ConsumeHeldItem(TSPlayer player, int amount)
    {
        var selectedSlot = player.TPlayer.selectedItem;

        if (selectedSlot < 0 || selectedSlot >= player.TPlayer.inventory.Length)
        {
            return null;
        }

        var item = player.TPlayer.inventory[selectedSlot];

        if (item == null || item.IsAir || item.type <= 0 || item.stack <= 0)
        {
            return null;
        }

        if (item.stack < amount)
        {
            return null;
        }

        var consumed = new ConsumedItem
        {
            Slot = selectedSlot,
            Type = item.type,
            Stack = amount,
            Prefix = item.prefix,
            Name = item.Name
        };

        item.stack -= amount;

        if (item.stack <= 0)
        {
            item.SetDefaults(0);
            item.stack = 0;
            item.prefix = 0;
        }

        SyncInventorySlot(player, selectedSlot);

        player.SendInfoMessage($"Se consumieron {amount}x {consumed.Name}.");

        return consumed;
    }

    private List<ConsumedItem> ConsumeGroupPlan(TSPlayer player, GroupDeliveryPlan plan)
    {
        var consumedItems = new List<ConsumedItem>();

        foreach (var planned in plan.ItemsToConsume)
        {
            if (planned.Slot < 0 || planned.Slot >= player.TPlayer.inventory.Length)
            {
                return new List<ConsumedItem>();
            }

            var item = player.TPlayer.inventory[planned.Slot];

            if (
                item == null ||
                item.IsAir ||
                item.type != planned.Type ||
                item.stack < planned.Stack)
            {
                return new List<ConsumedItem>();
            }
        }

        foreach (var planned in plan.ItemsToConsume)
        {
            var item = player.TPlayer.inventory[planned.Slot];

            consumedItems.Add(new ConsumedItem
            {
                Slot = planned.Slot,
                Type = item.type,
                Stack = planned.Stack,
                Prefix = item.prefix,
                Name = item.Name
            });

            item.stack -= planned.Stack;

            if (item.stack <= 0)
            {
                item.SetDefaults(0);
                item.stack = 0;
                item.prefix = 0;
            }

            SyncInventorySlot(player, planned.Slot);
        }

        return consumedItems;
    }

    private void RefundConsumedItems(TSPlayer player, List<ConsumedItem> consumedItems)
    {
        if (consumedItems.Count <= 0)
        {
            return;
        }

        foreach (var consumed in consumedItems.AsEnumerable().Reverse())
        {
            RefundConsumedItem(player, consumed);
        }

        player.SendErrorMessage("El bot rechazó la misión después de consumir items. Se intentó devolver lo consumido.");
    }

    private void RefundConsumedItem(TSPlayer player, ConsumedItem consumed)
    {
        if (TryRefundToSlot(player, consumed.Slot, consumed))
        {
            return;
        }

        for (var slot = 0; slot < player.TPlayer.inventory.Length; slot++)
        {
            if (TryRefundToSlot(player, slot, consumed))
            {
                return;
            }
        }

        player.SendErrorMessage($"No se pudo devolver automáticamente {consumed.Stack}x item ID {consumed.Type}. Contacta a un admin.");
    }

    private bool TryRefundToSlot(TSPlayer player, int slot, ConsumedItem consumed)
    {
        if (slot < 0 || slot >= player.TPlayer.inventory.Length)
        {
            return false;
        }

        var item = player.TPlayer.inventory[slot];

        if (item == null)
        {
            return false;
        }

        if (item.IsAir || item.type <= 0 || item.stack <= 0)
        {
            item.SetDefaults(consumed.Type);
            item.prefix = consumed.Prefix;
            item.stack = consumed.Stack;

            SyncInventorySlot(player, slot);
            return true;
        }

        if (item.type == consumed.Type && item.prefix == consumed.Prefix)
        {
            item.stack += consumed.Stack;

            SyncInventorySlot(player, slot);
            return true;
        }

        return false;
    }

    private void SyncInventorySlot(TSPlayer player, int slot)
    {
        if (slot < 0 || slot >= player.TPlayer.inventory.Length)
        {
            return;
        }

        var item = player.TPlayer.inventory[slot];

        if (item == null)
        {
            return;
        }

        NetMessage.SendData(
            (int)PacketTypes.PlayerSlot,
            -1,
            -1,
            null,
            player.Index,
            slot,
            item.stack,
            item.prefix,
            item.type
        );

        NetMessage.SendData(
            (int)PacketTypes.PlayerSlot,
            player.Index,
            -1,
            null,
            player.Index,
            slot,
            item.stack,
            item.prefix,
            item.type
        );
    }

    private async System.Threading.Tasks.Task<BridgeResponse> SendMissionPrepareAsync(
        BridgeProfile profile,
        string userId,
        string missionId)
    {
        var prepareEndpoint = GetPrepareEndpoint(profile.Endpoint);

        return await SendBridgeRequestAsync(
            endpoint: prepareEndpoint,
            profile: profile,
            userId: userId,
            missionId: missionId);
    }

    private async System.Threading.Tasks.Task<BridgeResponse> SendMissionCompleteAsync(
        BridgeProfile profile,
        string userId,
        string missionId)
    {
        return await SendBridgeRequestAsync(
            endpoint: profile.Endpoint,
            profile: profile,
            userId: userId,
            missionId: missionId);
    }

    private string GetPrepareEndpoint(string completeEndpoint)
    {
        var endpoint = (completeEndpoint ?? "").Trim();

        if (endpoint.EndsWith("/terraria/mission-complete", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint[..^"/terraria/mission-complete".Length] + "/terraria/mission-prepare";
        }

        if (endpoint.EndsWith("/mission-complete", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint[..^"/mission-complete".Length] + "/mission-prepare";
        }

        return endpoint.TrimEnd('/') + "/terraria/mission-prepare";
    }

    private async System.Threading.Tasks.Task<BridgeResponse> SendBridgeRequestAsync(
        string endpoint,
        BridgeProfile profile,
        string userId,
        string missionId)
    {
        var payload = new BridgeRequest
        {
            GuildId = profile.GuildId,
            UserId = userId,
            MissionId = missionId
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.Secret);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            BridgeResponse? parsed = null;

            try
            {
                parsed = JsonSerializer.Deserialize<BridgeResponse>(content, JsonOptions);
            }
            catch
            {
                // Si el bot responde algo no JSON, abajo devolvemos error genérico.
            }

            if (parsed != null)
            {
                parsed.StatusCode = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(parsed.Error))
                {
                    parsed.Error = response.StatusCode.ToString();
                }

                return parsed;
            }

            return new BridgeResponse
            {
                Ok = false,
                StatusCode = (int)response.StatusCode,
                Error = response.StatusCode.ToString(),
                Message = string.IsNullOrWhiteSpace(content) ? "Respuesta vacía del bot." : content
            };
        }
        catch (Exception ex)
        {
            return new BridgeResponse
            {
                Ok = false,
                Error = "REQUEST_FAILED",
                Message = ex.Message
            };
        }
    }

    private string GetFriendlyBridgeError(BridgeResponse response)
    {
        var error = response.Error ?? "UNKNOWN";

        return error switch
        {
            "MISSION_NOT_FOUND" => "Esa misión no existe en el bot.",
            "MISSION_INACTIVE" => "Esa misión está inactiva en el bot.",
            "ALREADY_COMPLETED" => "Ya completaste esa misión. No se consumió el item.",
            "UNAUTHORIZED" => "El plugin no está autorizado. Revisa secret en profiles.txt y TERRARIA_BRIDGE_SECRET en el bot.",
            "MISSING_GUILD_ID" => "Falta guildId. Revisa profiles.txt.",
            "MISSING_USER_ID" => "Falta userId. Revisa tu vinculación con /discord.",
            "MISSING_MISSION_ID" => "Falta missionId. Revisa missions.txt.",
            "REQUEST_FAILED" => $"No se pudo conectar con el bot: {response.Message}",
            _ => response.Message ?? $"El bot rechazó la misión. Error: {error}"
        };
    }

    private void SaveConfig()
    {
        var lines = new List<string>
        {
            "# Config general del plugin",
            "",
            $"Enabled = {_config.Enabled.ToString().ToLowerInvariant()}",
            $"DefaultProfile = {_config.DefaultProfile}",
            $"ConsumeItemOnSuccess = {_config.ConsumeItemOnSuccess.ToString().ToLowerInvariant()}"
        };

        File.WriteAllLines(ConfigPath, lines, Encoding.UTF8);
    }

    private void EnableCommand(CommandArgs args)
    {
        lock (_sync)
        {
            _config.Enabled = true;
            SaveConfig();
        }

        args.Player.SendSuccessMessage("Sistema de entregas activado.");
    }

    private void DisableCommand(CommandArgs args)
    {
        lock (_sync)
        {
            _config.Enabled = false;
            SaveConfig();
        }

        args.Player.SendSuccessMessage("Sistema de entregas desactivado temporalmente.");
    }

    private void StatusCommand(CommandArgs args)
    {
        var status = _config.Enabled ? "Activado" : "Desactivado";
        var consume = _config.ConsumeItemOnSuccess ? "Sí" : "No";

        args.Player.SendInfoMessage($"Sistema de entregas: {status}");
        args.Player.SendInfoMessage($"Perfil por defecto: {_config.DefaultProfile}");
        args.Player.SendInfoMessage($"Consumir items al completar: {consume}");
        args.Player.SendInfoMessage($"Misiones simples: {_missionsByItemId.Values.Sum(list => list.Count)}");
        args.Player.SendInfoMessage($"Misiones múltiples: {_missionGroups.Count}");
    }

    private void ItemInfoCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        var item = GetHeldItem(player);

        if (item == null || item.IsAir || item.type <= 0)
        {
            player.SendErrorMessage("No tienes ningún item válido en la mano.");
            return;
        }

        player.SendInfoMessage($"Item en mano: {item.Name} | ID: {item.type} | Cantidad: {item.stack}");
    }

    private void ReloadCommand(CommandArgs args)
    {
        try
        {
            EnsureFiles();
            ReloadFiles();

            args.Player.SendSuccessMessage("TerrariaMissionBridge recargado correctamente.");
            args.Player.SendInfoMessage($"Estado: {(_config.Enabled ? "Activado" : "Desactivado")}");
            args.Player.SendInfoMessage($"Perfiles: {_profiles.Count} | Simples: {_missionsByItemId.Values.Sum(list => list.Count)} | Múltiples: {_missionGroups.Count} | Jugadores vinculados: {_players.Count}");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[TerrariaMissionBridge] Error recargando: {ex}");
            args.Player.SendErrorMessage("No se pudo recargar el plugin. Revisa consola.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class BridgeConfig
    {
        public bool Enabled { get; set; } = true;
        public string DefaultProfile { get; set; } = "main";
        public bool ConsumeItemOnSuccess { get; set; } = true;
    }

    private sealed class BridgeProfile
    {
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Secret { get; set; } = "";
        public string GuildId { get; set; } = "";
    }

    private sealed class MissionEntry
    {
        public int ItemId { get; set; }
        public string MissionId { get; set; } = "";
        public int Amount { get; set; } = 1;
        public string ProfileName { get; set; } = "main";
    }

    private sealed class MissionGroup
    {
        public string MissionId { get; set; } = "";
        public string Mode { get; set; } = "group_all";
        public int RequiredOptions { get; set; } = 1;
        public string ProfileName { get; set; } = "main";
    }

    private sealed class GroupRequirement
    {
        public string MissionId { get; set; } = "";
        public string OptionId { get; set; } = "";
        public int ItemId { get; set; }
        public int Amount { get; set; } = 1;
        public string Label { get; set; } = "";
    }

    private sealed class GroupDeliveryPlan
    {
        public string MissionId { get; set; } = "";
        public List<GroupRequirement> SelectedRequirements { get; set; } = new();
        public List<ConsumedItem> ItemsToConsume { get; set; } = new();
    }

    private sealed class InventorySlotState
    {
        public int Slot { get; set; }
        public int Type { get; set; }
        public int StackRemaining { get; set; }
        public byte Prefix { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class PlayerLink
    {
        public string PlayerName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
    }

    private sealed class ConsumedItem
    {
        public int Slot { get; set; }
        public int Type { get; set; }
        public int Stack { get; set; }
        public byte Prefix { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class BridgeRequest
    {
        [JsonPropertyName("guildId")]
        public string GuildId { get; set; } = "";

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = "";

        [JsonPropertyName("missionId")]
        public string MissionId { get; set; } = "";
    }

    private sealed class BridgeResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("missionTitle")]
        public string? MissionTitle { get; set; }

        [JsonPropertyName("rewardText")]
        public string? RewardText { get; set; }

        public int StatusCode { get; set; }
    }
}