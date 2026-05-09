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
    private readonly Dictionary<string, string> _players = new(StringComparer.OrdinalIgnoreCase);

    private string PluginDirectory => Path.Combine(TShock.SavePath, "TerrariaMissionBridge");
    private string ConfigPath => Path.Combine(PluginDirectory, "config.txt");
    private string ProfilesPath => Path.Combine(PluginDirectory, "profiles.txt");
    private string MissionsPath => Path.Combine(PluginDirectory, "missions.txt");
    private string PlayersPath => Path.Combine(PluginDirectory, "players.txt");

    public override string Name => "TerrariaMissionBridge";
    public override string Author => "Rumic Bot / OpenAI";
    public override string Description => "Conecta entregas de items de Terraria con misiones de un bot de Discord.";
    public override Version Version => new Version(1, 0, 0);

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
            HelpText = "Entrega el item en tu mano si coincide con una misión configurada."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, ItemInfoCommand, "mbitem")
        {
            HelpText = "Muestra el ID, nombre y cantidad del item en tu mano."
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, ReloadCommand, "mbreload")
        {
            HelpText = "Recarga config.txt, profiles.txt, missions.txt y players.txt."
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
                command.Names.Contains("mbreload"));
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
                    "# itemId | missionId | amount | profile",
                    "# profile es opcional. Si no lo pones, usa DefaultProfile.",
                    "",
                    "29 | madera_entregada | 10 | main",
                    "75 | espada_hierro_entregada | 1 | main"
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

        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type <= 0 || heldItem.stack <= 0)
        {
            player.SendErrorMessage("Debes tener en la mano el item que quieres entregar.");
            return;
        }

        MissionEntry? mission = null;
        BridgeProfile? profile = null;

        lock (_sync)
        {
            if (_missionsByItemId.TryGetValue(heldItem.type, out var missions))
            {
                mission = missions.FirstOrDefault(entry => heldItem.stack >= entry.Amount);

                if (mission != null)
                {
                    _profiles.TryGetValue(mission.ProfileName, out profile);
                }
            }
        }

        if (mission == null)
        {
            player.SendErrorMessage($"El item en tu mano no está configurado como misión. ID del item: {heldItem.type}");
            return;
        }

        if (heldItem.stack < mission.Amount)
        {
            player.SendErrorMessage($"Necesitas {mission.Amount}x {heldItem.Name}. Tienes {heldItem.stack}.");
            return;
        }

        if (profile == null)
        {
            player.SendErrorMessage($"La misión usa el perfil '{mission.ProfileName}', pero ese perfil no existe en profiles.txt.");
            return;
        }

        player.SendInfoMessage($"Entregando misión '{mission.MissionId}' al bot...");

        var response = await SendMissionCompleteAsync(profile, discordUserId, mission.MissionId);

        if (!response.Ok)
        {
            player.SendErrorMessage(GetFriendlyBridgeError(response));
            return;
        }

        if (_config.ConsumeItemOnSuccess)
        {
            ConsumeHeldItem(player, mission.Amount);
        }

        player.SendSuccessMessage($"Misión completada: {response.MissionTitle ?? mission.MissionId}");
        if (!string.IsNullOrWhiteSpace(response.RewardText))
        {
            player.SendInfoMessage(response.RewardText);
        }
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

    private void ConsumeHeldItem(TSPlayer player, int amount)
    {
        var selectedSlot = player.TPlayer.selectedItem;

        if (selectedSlot < 0 || selectedSlot >= player.TPlayer.inventory.Length)
        {
            return;
        }

        var item = player.TPlayer.inventory[selectedSlot];

        if (item == null || item.IsAir)
        {
            return;
        }

        item.stack -= amount;

        if (item.stack <= 0)
        {
            item.TurnToAir();
        }

        player.SendData(PacketTypes.PlayerSlot, "", player.Index, selectedSlot);
    }

    private async System.Threading.Tasks.Task<BridgeResponse> SendMissionCompleteAsync(
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
        using var request = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);

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
            args.Player.SendInfoMessage($"Perfiles: {_profiles.Count} | Items con misión: {_missionsByItemId.Count} | Jugadores vinculados: {_players.Count}");
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

    private sealed class PlayerLink
    {
        public string PlayerName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
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