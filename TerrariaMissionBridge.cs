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
    private readonly Dictionary<string, string> _messages = new(StringComparer.OrdinalIgnoreCase);

    private string PluginDirectory => Path.Combine(TShock.SavePath, "TerrariaMissionBridge");
    private string ConfigPath => Path.Combine(PluginDirectory, "config.txt");
    private string ProfilesPath => Path.Combine(PluginDirectory, "profiles.txt");
    private string MissionsPath => Path.Combine(PluginDirectory, "missions.txt");
    private string MissionGroupsPath => Path.Combine(PluginDirectory, "mission_groups.txt");
    private string MissionRequirementsPath => Path.Combine(PluginDirectory, "mission_requirements.txt");
    private string PlayersPath => Path.Combine(PluginDirectory, "players.txt");
    private string MessagesPath => Path.Combine(PluginDirectory, "messages.txt");

    public override string Name => "TerrariaMissionBridge";
    public override string Author => "Rumic Bot / OpenAI";
    public override string Description => "Conecta entregas de items de Terraria con misiones de un bot de Discord.";
    public override Version Version => new Version(1, 7, 0);

    public TerrariaMissionBridgePlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        EnsureFiles();
        ReloadFiles();

        Commands.ChatCommands.Add(new Command(DiscordCommand, "discord")
        {
            HelpText = "Vincula tu personaje con Discord. Uso: /discord <codigo>"
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

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, AddSimpleCommand, "mbaddsimple")
        {
            HelpText = "Agrega una misión simple usando el item en mano. Uso: /mbaddsimple <missionId> <amount> [profile]"
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, DeleteSimpleCommand, "mbdelsimple")
        {
            HelpText = "Elimina una misión simple. Uso: /mbdelsimple <missionId>"
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, AddGroupCommand, "mbaddgroup")
        {
            HelpText = "Agrega una misión múltiple. Uso: /mbaddgroup <missionId> <group_any|group_all> <requiredOptions> [profile]"
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, DeleteGroupCommand, "mbdelgroup")
        {
            HelpText = "Elimina una misión múltiple y sus requisitos. Uso: /mbdelgroup <missionId>"
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, AddRequirementCommand, "mbaddreq")
        {
            HelpText = "Agrega un requisito usando el item en mano. Uso: /mbaddreq <missionId> <optionId> <amount> [label...]"
        });

        Commands.ChatCommands.Add(new Command(PluginPermissionAdmin, DeleteRequirementCommand, "mbdelreq")
        {
            HelpText = "Elimina un requisito de misión múltiple. Uso: /mbdelreq <missionId> <optionId>"
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
                command.Names.Contains("mbstatus") ||
                command.Names.Contains("mbaddsimple") ||
                command.Names.Contains("mbdelsimple") ||
                command.Names.Contains("mbaddgroup") ||
                command.Names.Contains("mbdelgroup") ||
                command.Names.Contains("mbaddreq") ||
                command.Names.Contains("mbdelreq"));
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
                    "# El plugin calculará /terraria/mission-prepare y /terraria/link-verify automáticamente.",
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
                    "# Este archivo se llena usando /discord <codigo>.",
                    ""
                }) + Environment.NewLine,
                Encoding.UTF8);
        }

        if (!File.Exists(MessagesPath))
        {
            File.WriteAllLines(MessagesPath, BuildDefaultMessagesFileLines(), Encoding.UTF8);
        }
    }
private List<string> BuildDefaultMessagesFileLines()
    {
        var lines = new List<string>
        {
            "# TerrariaMissionBridge messages",
            "# Soporta colores de TShock:",
            "# [c/57f287:Texto verde]",
            "# [c/ed4245:Texto rojo]",
            "# [c/f5a9b8:Texto rosa]",
            "#",
            "# Placeholders disponibles:",
            "# {player}, {code}, {missionId}, {missionTitle}, {itemName}, {itemId}, {amount}, {label}",
            "# {rewardText}, {status}, {profile}, {count}, {consume}, {error}, {message}",
            "# {optionId}, {mode}, {profiles}, {simple}, {groups}, {players}, {requirements}",
            ""
        };

        foreach (var pair in GetDefaultMessages())
        {
            lines.Add($"{pair.Key} = {pair.Value}");
        }

        return lines;
    }

    private Dictionary<string, string> GetDefaultMessages()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["system_disabled"] = "[c/ed4245:⛔ El sistema de entregas está desactivado temporalmente.]",

            ["discord_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/discord <codigo>]",
            ["discord_invalid_code"] = "[c/ed4245:❌ Código inválido.] [c/ffffff:Genera uno desde Discord con linkterraria.]",
            ["discord_missing_profile"] = "[c/ed4245:❌ No existe el perfil por defecto] [c/ffffff:{profile}] [c/ed4245:en profiles.txt.]",
            ["discord_verifying"] = "[c/57b8ff:🔎 Verificando código con Discord...]",
            ["discord_link_success"] = "[c/57f287:✅ Tu personaje quedó vinculado correctamente con tu cuenta de Discord.]",
            ["discord_link_error"] = "[c/ed4245:❌ Ocurrió un error vinculando tu cuenta.]",

            ["deliver_need_link"] = "[c/ffcc00:🔗 Primero vincula tu Discord con:] [c/ffffff:/discord <codigo>]",
            ["deliver_no_ready_mission"] = "[c/ed4245:❌ No tienes ningún item o conjunto de items listo para entregar.]",
            ["deliver_no_pending_mission"] = "[c/ffcc00:⚠️ Tienes items de misiones detectadas, pero ninguna misión pendiente disponible para completar. Puede que sean misiones ya completadas, inactivas o inexistentes en el bot.]",
            ["deliver_error"] = "[c/ed4245:❌ Ocurrió un error entregando la misión.]",
            ["deliver_simple_missing_hand"] = "[c/ed4245:❌ Ya no tienes el item requerido en la mano.]",
            ["deliver_validating"] = "[c/57b8ff:🔎 Validando misión] [c/ffffff:{missionId}] [c/57b8ff:con el bot...]",
            ["deliver_validating_group"] = "[c/57b8ff:🔎 Validando misión múltiple] [c/ffffff:{missionId}] [c/57b8ff:con el bot...]",
            ["deliver_revalidate_failed"] = "[c/ed4245:❌ La misión fue validada, pero ya no tienes el item requerido. No se completó ni se consumió nada.]",
            ["deliver_revalidate_group_failed"] = "[c/ed4245:❌ La misión fue validada, pero ya no tienes los items requeridos. No se completó ni se consumió nada.]",
            ["deliver_consume_failed"] = "[c/ed4245:❌ No pude consumir el item requerido. No se completó la misión.]",
            ["deliver_consume_group_failed"] = "[c/ed4245:❌ No pude consumir los items requeridos. No se completó la misión.]",
            ["deliver_completing"] = "[c/f5a9b8:📨 Completando misión] [c/ffffff:{missionId}] [c/f5a9b8:con el bot...]",
            ["deliver_completing_group"] = "[c/f5a9b8:📨 Completando misión múltiple] [c/ffffff:{missionId}] [c/f5a9b8:con el bot...]",
            ["deliver_success"] = "[c/57f287:✅ Misión completada:] [c/ffffff:{missionTitle}]",
            ["deliver_items_title"] = "[c/f5a9b8:📦 Items entregados:]",
            ["deliver_item_line"] = "[c/ffffff:- {amount}x {label}]",

            ["item_consumed"] = "[c/f5a9b8:📦 Se consumieron] [c/ffffff:{amount}x {itemName}].",
            ["item_no_valid_hand"] = "[c/ed4245:❌ No tienes ningún item válido en la mano.]",
            ["item_info"] = "[c/57b8ff:Item en mano:] [c/ffffff:{itemName}] [c/57b8ff:| ID:] [c/ffffff:{itemId}] [c/57b8ff:| Cantidad:] [c/ffffff:{amount}]",
            ["refund_attempt"] = "[c/ffcc00:⚠️ El bot rechazó la misión después de consumir items. Se intentó devolver lo consumido.]",
            ["refund_failed"] = "[c/ed4245:❌ No se pudo devolver automáticamente {amount}x item ID {itemId}. Contacta a un admin.]",

            ["reload_success"] = "[c/57f287:✅ TerrariaMissionBridge recargado correctamente.]",
            ["reload_error"] = "[c/ed4245:❌ No se pudo recargar el plugin. Revisa consola.]",
            ["reload_status"] = "[c/57b8ff:Estado:] {status}",
            ["reload_counts"] = "[c/57b8ff:Perfiles:] [c/ffffff:{profiles}] [c/57b8ff:| Simples:] [c/ffffff:{simple}] [c/57b8ff:| Múltiples:] [c/ffffff:{groups}] [c/57b8ff:| Jugadores vinculados:] [c/ffffff:{players}]",

            ["status_enabled"] = "[c/57f287:Activado]",
            ["status_disabled"] = "[c/ed4245:Desactivado]",
            ["status_yes"] = "Sí",
            ["status_no"] = "No",
            ["status_line_system"] = "[c/57b8ff:Sistema de entregas:] {status}",
            ["status_line_profile"] = "[c/57b8ff:Perfil por defecto:] [c/ffffff:{profile}]",
            ["status_line_consume"] = "[c/57b8ff:Consumir items al completar:] [c/ffffff:{consume}]",
            ["status_line_simple"] = "[c/57b8ff:Misiones simples:] [c/ffffff:{count}]",
            ["status_line_group"] = "[c/57b8ff:Misiones múltiples:] [c/ffffff:{count}]",

            ["admin_enabled"] = "[c/57f287:✅ Sistema de entregas activado.]",
            ["admin_disabled"] = "[c/ffcc00:⚠️ Sistema de entregas desactivado temporalmente.]",
            ["admin_item_required"] = "[c/ed4245:❌ Debes tener un item válido en la mano.]",
            ["admin_invalid_amount"] = "[c/ed4245:❌ La cantidad debe ser un número entero mayor que 0.]",
            ["admin_invalid_mode"] = "[c/ed4245:❌ Modo inválido. Usa group_any o group_all.]",
            ["admin_duplicate_simple"] = "[c/ffcc00:⚠️ Ya existe una misión simple con ID] [c/ffffff:{missionId}][c/ffcc00:.]",
            ["admin_duplicate_group"] = "[c/ffcc00:⚠️ Ya existe una misión múltiple con ID] [c/ffffff:{missionId}][c/ffcc00:.]",
            ["admin_duplicate_requirement"] = "[c/ffcc00:⚠️ Ya existe el requisito] [c/ffffff:{optionId}] [c/ffcc00:para la misión] [c/ffffff:{missionId}][c/ffcc00:.]",
            ["admin_group_missing"] = "[c/ed4245:❌ No existe una misión múltiple con ID] [c/ffffff:{missionId}][c/ed4245:. Crea primero el grupo con /mbaddgroup.]",
            ["admin_nothing_deleted"] = "[c/ffcc00:⚠️ No se encontró nada para eliminar.]",

            ["admin_addsimple_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbaddsimple <missionId> <amount> [profile]]",
            ["admin_addsimple_success"] = "[c/57f287:✅ Misión simple agregada:] [c/ffffff:{missionId}] [c/57b8ff:| Item:] [c/ffffff:{itemName}] [c/57b8ff:ID:] [c/ffffff:{itemId}] [c/57b8ff:Cantidad:] [c/ffffff:{amount}] [c/57b8ff:Perfil:] [c/ffffff:{profile}]",
            ["admin_delsimple_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbdelsimple <missionId>]",
            ["admin_delsimple_success"] = "[c/57f287:✅ Misión simple eliminada:] [c/ffffff:{missionId}] [c/57b8ff:Líneas borradas:] [c/ffffff:{count}]",

            ["admin_addgroup_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbaddgroup <missionId> <group_any|group_all> <requiredOptions> [profile]]",
            ["admin_addgroup_success"] = "[c/57f287:✅ Misión múltiple agregada:] [c/ffffff:{missionId}] [c/57b8ff:Modo:] [c/ffffff:{mode}] [c/57b8ff:Opciones requeridas:] [c/ffffff:{amount}] [c/57b8ff:Perfil:] [c/ffffff:{profile}]",
            ["admin_delgroup_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbdelgroup <missionId>]",
            ["admin_delgroup_success"] = "[c/57f287:✅ Misión múltiple eliminada:] [c/ffffff:{missionId}] [c/57b8ff:Grupos borrados:] [c/ffffff:{groups}] [c/57b8ff:Requisitos borrados:] [c/ffffff:{requirements}]",

            ["admin_addreq_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbaddreq <missionId> <optionId> <amount> [label...]]",
            ["admin_addreq_success"] = "[c/57f287:✅ Requisito agregado:] [c/ffffff:{missionId}] [c/57b8ff:| Opción:] [c/ffffff:{optionId}] [c/57b8ff:| Item:] [c/ffffff:{itemName}] [c/57b8ff:ID:] [c/ffffff:{itemId}] [c/57b8ff:Cantidad:] [c/ffffff:{amount}]",
            ["admin_delreq_usage"] = "[c/ffcc00:Uso:] [c/ffffff:/mbdelreq <missionId> <optionId>]",
            ["admin_delreq_success"] = "[c/57f287:✅ Requisito eliminado:] [c/ffffff:{missionId}] [c/57b8ff:| Opción:] [c/ffffff:{optionId}] [c/57b8ff:Líneas borradas:] [c/ffffff:{count}]",

            ["error_mission_not_found"] = "[c/ed4245:❌ Esa misión no existe en el bot.]",
            ["error_mission_inactive"] = "[c/ed4245:❌ Esa misión está inactiva en el bot.]",
            ["error_already_completed"] = "[c/ffcc00:⚠️ Ya completaste esa misión. No se consumió el item.]",
            ["error_unauthorized"] = "[c/ed4245:❌ El plugin no está autorizado. Revisa secret en profiles.txt y TERRARIA_BRIDGE_SECRET en el bot.]",
            ["error_missing_guild"] = "[c/ed4245:❌ Falta guildId. Revisa profiles.txt.]",
            ["error_missing_user"] = "[c/ed4245:❌ Falta userId. Revisa tu vinculación con /discord.]",
            ["error_missing_mission"] = "[c/ed4245:❌ Falta missionId. Revisa missions.txt.]",
            ["error_missing_code"] = "[c/ed4245:❌ Falta el código de vinculación.]",
            ["error_code_not_found"] = "[c/ed4245:❌ Ese código no existe. Genera uno nuevo desde Discord con linkterraria.]",
            ["error_code_used"] = "[c/ffcc00:⚠️ Ese código ya fue usado. Genera uno nuevo desde Discord.]",
            ["error_code_expired"] = "[c/ffcc00:⚠️ Ese código ya expiró. Genera uno nuevo desde Discord.]",
            ["error_link_system"] = "[c/ed4245:❌ El sistema de vinculación no está disponible en el bot.]",
            ["error_request_failed"] = "[c/ed4245:❌ No se pudo conectar con el bot:] [c/ffffff:{message}]",
            ["error_unknown"] = "[c/ed4245:❌ El bot rechazó la solicitud.] [c/ffffff:Error: {error}]"
        };
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
            _messages.Clear();

            foreach (var pair in GetDefaultMessages())
            {
                _messages[pair.Key] = pair.Value;
            }

            foreach (var pair in LoadMessages())
            {
                _messages[pair.Key] = pair.Value;
            }

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

    private Dictionary<string, string> LoadMessages()
    {
        var messages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(MessagesPath))
        {
            return messages;
        }

        foreach (var line in ReadUsefulLines(MessagesPath))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0];
            var value = parts[1];

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            messages[key] = value;
        }

        return messages;
    }

    private string Msg(string key)
    {
        lock (_sync)
        {
            return _messages.TryGetValue(key, out var value) ? value : key;
        }
    }

    private string Msg(string key, Dictionary<string, string?> placeholders)
    {
        var text = Msg(key);

        foreach (var pair in placeholders)
        {
            text = text.Replace("{" + pair.Key + "}", pair.Value ?? "");
        }

        return text;
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

    private async void DiscordCommand(CommandArgs args)
    {
        try
        {
            await DiscordCommandAsync(args);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[TerrariaMissionBridge] Error en /discord: {ex}");
            args.Player?.SendErrorMessage(Msg("discord_link_error"));
        }
    }

    private async System.Threading.Tasks.Task DiscordCommandAsync(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 1)
        {
            player.SendErrorMessage(Msg("discord_usage"));
            return;
        }

        var code = args.Parameters[0].Trim().ToUpperInvariant();

        if (!IsValidLinkCode(code))
        {
            player.SendErrorMessage(Msg("discord_invalid_code"));
            return;
        }

        BridgeProfile? profile = null;

        lock (_sync)
        {
            _profiles.TryGetValue(_config.DefaultProfile, out profile);
        }

        if (profile == null)
        {
            player.SendErrorMessage(Msg("discord_missing_profile", new Dictionary<string, string?>
            {
                ["profile"] = _config.DefaultProfile
            }));
            return;
        }

        player.SendInfoMessage(Msg("discord_verifying"));

        var response = await SendLinkVerifyAsync(profile, code);

        if (!response.Ok || string.IsNullOrWhiteSpace(response.UserId))
        {
            player.SendErrorMessage(GetFriendlyBridgeError(response));
            return;
        }

        lock (_sync)
        {
            _players[player.Name] = response.UserId;
            SavePlayers();
        }

        player.SendSuccessMessage(Msg("discord_link_success"));
    }

    private bool IsValidLinkCode(string value)
    {
        var text = (value ?? "").Trim();

        if (text.Length < 4 || text.Length > 20)
        {
            return false;
        }

        return text.All(char.IsLetterOrDigit);
    }

    private void SavePlayers()
    {
        var lines = new List<string>
        {
            "# playerName | discordUserId",
            "# Este archivo se llena usando /discord <codigo>.",
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
            args.Player?.SendErrorMessage(Msg("deliver_error"));
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
            player.SendErrorMessage(Msg("system_disabled"));
            return;
        }

        string? discordUserId;

        lock (_sync)
        {
            _players.TryGetValue(player.Name, out discordUserId);
        }

        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            player.SendErrorMessage(Msg("deliver_need_link"));
            return;
        }

        var attemptedAnyCandidate = false;

        foreach (var candidate in FindSimpleMissionCandidates(player))
        {
            attemptedAnyCandidate = true;

            var result = await TryDeliverSimpleMissionAsync(
                player,
                discordUserId,
                candidate.Mission,
                candidate.Profile);

            if (result.Completed)
            {
                return;
            }

            if (result.ShouldTryNext)
            {
                continue;
            }

            return;
        }

        foreach (var candidate in FindGroupMissionCandidates(player))
        {
            attemptedAnyCandidate = true;

            var result = await TryDeliverGroupMissionAsync(
                player,
                discordUserId,
                candidate.Group,
                candidate.Profile,
                candidate.Plan);

            if (result.Completed)
            {
                return;
            }

            if (result.ShouldTryNext)
            {
                continue;
            }

            return;
        }

        if (attemptedAnyCandidate)
        {
            player.SendErrorMessage(Msg("deliver_no_pending_mission"));
            return;
        }

        player.SendErrorMessage(Msg("deliver_no_ready_mission"));
    }

    private List<SimpleMissionCandidate> FindSimpleMissionCandidates(TSPlayer player)
    {
        var candidates = new List<SimpleMissionCandidate>();
        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type <= 0 || heldItem.stack <= 0)
        {
            return candidates;
        }

        lock (_sync)
        {
            if (!_missionsByItemId.TryGetValue(heldItem.type, out var missions))
            {
                return candidates;
            }

            foreach (var mission in missions)
            {
                if (heldItem.stack < mission.Amount)
                {
                    continue;
                }

                if (!_profiles.TryGetValue(mission.ProfileName, out var profile))
                {
                    continue;
                }

                candidates.Add(new SimpleMissionCandidate
                {
                    Mission = mission,
                    Profile = profile
                });
            }
        }

        return candidates;
    }

    private List<GroupMissionCandidate> FindGroupMissionCandidates(TSPlayer player)
    {
        var candidates = new List<GroupMissionCandidate>();

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

        foreach (var group in groupsSnapshot)
        {
            if (!requirementsSnapshot.TryGetValue(group.MissionId, out var requirements))
            {
                continue;
            }

            if (!profilesSnapshot.TryGetValue(group.ProfileName, out var profile))
            {
                continue;
            }

            if (!TryBuildGroupDeliveryPlan(player, group, requirements, out var plan))
            {
                continue;
            }

            candidates.Add(new GroupMissionCandidate
            {
                Group = group,
                Profile = profile,
                Plan = plan
            });
        }

        return candidates;
    }

    private bool ShouldTryNextMission(BridgeResponse response)
    {
        var error = response.Error ?? "";

        return error.Equals("ALREADY_COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            error.Equals("MISSION_INACTIVE", StringComparison.OrdinalIgnoreCase) ||
            error.Equals("MISSION_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task<DeliveryAttemptResult> TryDeliverSimpleMissionAsync(
        TSPlayer player,
        string discordUserId,
        MissionEntry mission,
        BridgeProfile profile)
    {
        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type != mission.ItemId || heldItem.stack < mission.Amount)
        {
            player.SendErrorMessage(Msg("deliver_simple_missing_hand"));
            return DeliveryAttemptResult.Stop();
        }

        player.SendInfoMessage(Msg("deliver_validating", new Dictionary<string, string?>
        {
            ["missionId"] = mission.MissionId
        }));

        var prepareResponse = await SendMissionPrepareAsync(profile, discordUserId, mission.MissionId);

        if (!prepareResponse.Ok)
        {
            if (ShouldTryNextMission(prepareResponse))
            {
                return DeliveryAttemptResult.TryNext();
            }

            player.SendErrorMessage(GetFriendlyBridgeError(prepareResponse));
            return DeliveryAttemptResult.Stop();
        }

        var revalidatedItem = GetHeldItem(player);

        if (
            revalidatedItem == null ||
            revalidatedItem.IsAir ||
            revalidatedItem.type != mission.ItemId ||
            revalidatedItem.stack < mission.Amount)
        {
            player.SendErrorMessage(Msg("deliver_revalidate_failed"));
            return DeliveryAttemptResult.Stop();
        }

        var consumedItems = new List<ConsumedItem>();

        if (_config.ConsumeItemOnSuccess)
        {
            var consumedItem = ConsumeHeldItem(player, mission.Amount);

            if (consumedItem == null)
            {
                player.SendErrorMessage(Msg("deliver_consume_failed"));
                return DeliveryAttemptResult.Stop();
            }

            consumedItems.Add(consumedItem);
        }

        player.SendInfoMessage(Msg("deliver_completing", new Dictionary<string, string?>
        {
            ["missionId"] = mission.MissionId
        }));

        var completeResponse = await SendMissionCompleteAsync(profile, discordUserId, mission.MissionId);

        if (!completeResponse.Ok)
        {
            RefundConsumedItems(player, consumedItems);

            if (ShouldTryNextMission(completeResponse))
            {
                return DeliveryAttemptResult.TryNext();
            }

            player.SendErrorMessage(GetFriendlyBridgeError(completeResponse));
            return DeliveryAttemptResult.Stop();
        }

        player.SendSuccessMessage(Msg("deliver_success", new Dictionary<string, string?>
        {
            ["missionTitle"] = completeResponse.MissionTitle ?? mission.MissionId,
            ["missionId"] = mission.MissionId
        }));

        if (!string.IsNullOrWhiteSpace(completeResponse.RewardText))
        {
            player.SendInfoMessage(completeResponse.RewardText);
        }

        return DeliveryAttemptResult.CompletedResult();
    }

    private async System.Threading.Tasks.Task<DeliveryAttemptResult> TryDeliverGroupMissionAsync(
        TSPlayer player,
        string discordUserId,
        MissionGroup group,
        BridgeProfile profile,
        GroupDeliveryPlan initialPlan)
    {
        player.SendInfoMessage(Msg("deliver_validating_group", new Dictionary<string, string?>
        {
            ["missionId"] = group.MissionId
        }));

        var prepareResponse = await SendMissionPrepareAsync(profile, discordUserId, group.MissionId);

        if (!prepareResponse.Ok)
        {
            if (ShouldTryNextMission(prepareResponse))
            {
                return DeliveryAttemptResult.TryNext();
            }

            player.SendErrorMessage(GetFriendlyBridgeError(prepareResponse));
            return DeliveryAttemptResult.Stop();
        }

        List<GroupRequirement>? requirements;

        lock (_sync)
        {
            _requirementsByMissionId.TryGetValue(group.MissionId, out requirements);
            requirements = requirements?.ToList();
        }

        if (requirements == null || !TryBuildGroupDeliveryPlan(player, group, requirements, out var finalPlan))
        {
            player.SendErrorMessage(Msg("deliver_revalidate_group_failed"));
            return DeliveryAttemptResult.Stop();
        }

        List<ConsumedItem> consumedItems = new List<ConsumedItem>();

        if (_config.ConsumeItemOnSuccess)
        {
            consumedItems = ConsumeGroupPlan(player, finalPlan);

            if (consumedItems.Count <= 0)
            {
                player.SendErrorMessage(Msg("deliver_consume_group_failed"));
                return DeliveryAttemptResult.Stop();
            }
        }

        player.SendInfoMessage(Msg("deliver_completing_group", new Dictionary<string, string?>
        {
            ["missionId"] = group.MissionId
        }));

        var completeResponse = await SendMissionCompleteAsync(profile, discordUserId, group.MissionId);

        if (!completeResponse.Ok)
        {
            RefundConsumedItems(player, consumedItems);

            if (ShouldTryNextMission(completeResponse))
            {
                return DeliveryAttemptResult.TryNext();
            }

            player.SendErrorMessage(GetFriendlyBridgeError(completeResponse));
            return DeliveryAttemptResult.Stop();
        }

        player.SendSuccessMessage(Msg("deliver_success", new Dictionary<string, string?>
        {
            ["missionTitle"] = completeResponse.MissionTitle ?? group.MissionId,
            ["missionId"] = group.MissionId
        }));

        if (finalPlan.SelectedRequirements.Count > 0)
        {
            player.SendInfoMessage(Msg("deliver_items_title"));

            foreach (var requirement in finalPlan.SelectedRequirements)
            {
                player.SendInfoMessage(Msg("deliver_item_line", new Dictionary<string, string?>
                {
                    ["amount"] = requirement.Amount.ToString(),
                    ["label"] = requirement.Label,
                    ["itemId"] = requirement.ItemId.ToString(),
                    ["missionId"] = group.MissionId
                }));
            }
        }

        if (!string.IsNullOrWhiteSpace(completeResponse.RewardText))
        {
            player.SendInfoMessage(completeResponse.RewardText);
        }

        return DeliveryAttemptResult.CompletedResult();
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

        player.SendInfoMessage(Msg("item_consumed", new Dictionary<string, string?>
        {
            ["amount"] = amount.ToString(),
            ["itemName"] = consumed.Name,
            ["itemId"] = consumed.Type.ToString()
        }));

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

        player.SendErrorMessage(Msg("refund_attempt"));
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

        player.SendErrorMessage(Msg("refund_failed", new Dictionary<string, string?>
        {
            ["amount"] = consumed.Stack.ToString(),
            ["itemId"] = consumed.Type.ToString(),
            ["itemName"] = consumed.Name
        }));
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

    private async System.Threading.Tasks.Task<BridgeResponse> SendLinkVerifyAsync(
        BridgeProfile profile,
        string code)
    {
        var endpoint = GetLinkVerifyEndpoint(profile.Endpoint);

        var payload = new LinkVerifyRequest
        {
            GuildId = profile.GuildId,
            Code = code
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

    private string GetLinkVerifyEndpoint(string completeEndpoint)
    {
        var endpoint = (completeEndpoint ?? "").Trim();

        if (endpoint.EndsWith("/terraria/mission-complete", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint[..^"/terraria/mission-complete".Length] + "/terraria/link-verify";
        }

        if (endpoint.EndsWith("/mission-complete", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint[..^"/mission-complete".Length] + "/link-verify";
        }

        return endpoint.TrimEnd('/') + "/terraria/link-verify";
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
            "MISSION_NOT_FOUND" => Msg("error_mission_not_found"),
            "MISSION_INACTIVE" => Msg("error_mission_inactive"),
            "ALREADY_COMPLETED" => Msg("error_already_completed"),
            "UNAUTHORIZED" => Msg("error_unauthorized"),
            "MISSING_GUILD_ID" => Msg("error_missing_guild"),
            "MISSING_USER_ID" => Msg("error_missing_user"),
            "MISSING_MISSION_ID" => Msg("error_missing_mission"),
            "MISSING_CODE" => Msg("error_missing_code"),
            "CODE_NOT_FOUND" => Msg("error_code_not_found"),
            "CODE_ALREADY_USED" => Msg("error_code_used"),
            "CODE_EXPIRED" => Msg("error_code_expired"),
            "LINK_SYSTEM_NOT_AVAILABLE" => Msg("error_link_system"),
            "REQUEST_FAILED" => Msg("error_request_failed", new Dictionary<string, string?>
            {
                ["message"] = response.Message ?? ""
            }),
            _ => Msg("error_unknown", new Dictionary<string, string?>
            {
                ["error"] = error,
                ["message"] = response.Message ?? ""
            })
        };
    }
private string CleanField(string value)
    {
        return (value ?? "")
            .Replace("|", "/")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private void AppendConfigLine(string path, string line)
    {
        var content = File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : "";

        var separator = content.EndsWith("\n") || content.Length == 0 ? "" : Environment.NewLine;

        File.WriteAllText(
            path,
            content + separator + line + Environment.NewLine,
            Encoding.UTF8
        );
    }

    private int RemoveLinesFromFile(string path, Func<string, bool> shouldRemove)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var nextLines = new List<string>();
        var removed = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (
                string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith("#") ||
                !shouldRemove(trimmed))
            {
                nextLines.Add(line);
                continue;
            }

            removed++;
        }

        File.WriteAllLines(path, nextLines, Encoding.UTF8);
        return removed;
    }

    private bool SimpleMissionExists(string missionId)
    {
        lock (_sync)
        {
            return _missionsByItemId.Values
                .SelectMany(list => list)
                .Any(mission => mission.MissionId.Equals(missionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool GroupMissionExists(string missionId)
    {
        lock (_sync)
        {
            return _missionGroups.Any(group =>
                group.MissionId.Equals(missionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool RequirementExists(string missionId, string optionId)
    {
        lock (_sync)
        {
            return _requirementsByMissionId.TryGetValue(missionId, out var requirements) &&
                requirements.Any(requirement =>
                    requirement.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void AddSimpleCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 2)
        {
            player.SendErrorMessage(Msg("admin_addsimple_usage"));
            return;
        }

        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type <= 0)
        {
            player.SendErrorMessage(Msg("admin_item_required"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);

        if (!int.TryParse(args.Parameters[1], out var amount) || amount <= 0)
        {
            player.SendErrorMessage(Msg("admin_invalid_amount"));
            return;
        }

        var profile = args.Parameters.Count >= 3
            ? CleanField(args.Parameters[2])
            : _config.DefaultProfile;

        if (SimpleMissionExists(missionId))
        {
            player.SendErrorMessage(Msg("admin_duplicate_simple", new Dictionary<string, string?>
            {
                ["missionId"] = missionId
            }));
            return;
        }

        AppendConfigLine(
            MissionsPath,
            $"{heldItem.type} | {missionId} | {amount} | {profile}"
        );

        ReloadFiles();

        player.SendSuccessMessage(Msg("admin_addsimple_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["itemName"] = heldItem.Name,
            ["itemId"] = heldItem.type.ToString(),
            ["amount"] = amount.ToString(),
            ["profile"] = profile
        }));
    }

    private void DeleteSimpleCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 1)
        {
            player.SendErrorMessage(Msg("admin_delsimple_usage"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);

        var removed = RemoveLinesFromFile(MissionsPath, line =>
        {
            var parts = SplitPipe(line);
            return parts.Length >= 2 &&
                parts[1].Equals(missionId, StringComparison.OrdinalIgnoreCase);
        });

        ReloadFiles();

        if (removed <= 0)
        {
            player.SendErrorMessage(Msg("admin_nothing_deleted"));
            return;
        }

        player.SendSuccessMessage(Msg("admin_delsimple_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["count"] = removed.ToString()
        }));
    }

    private void AddGroupCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 3)
        {
            player.SendErrorMessage(Msg("admin_addgroup_usage"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);
        var mode = CleanField(args.Parameters[1]).ToLowerInvariant();

        if (mode != "group_any" && mode != "group_all")
        {
            player.SendErrorMessage(Msg("admin_invalid_mode"));
            return;
        }

        if (!int.TryParse(args.Parameters[2], out var requiredOptions) || requiredOptions <= 0)
        {
            player.SendErrorMessage(Msg("admin_invalid_amount"));
            return;
        }

        var profile = args.Parameters.Count >= 4
            ? CleanField(args.Parameters[3])
            : _config.DefaultProfile;

        if (GroupMissionExists(missionId))
        {
            player.SendErrorMessage(Msg("admin_duplicate_group", new Dictionary<string, string?>
            {
                ["missionId"] = missionId
            }));
            return;
        }

        AppendConfigLine(
            MissionGroupsPath,
            $"{missionId} | {mode} | {requiredOptions} | {profile}"
        );

        ReloadFiles();

        player.SendSuccessMessage(Msg("admin_addgroup_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["mode"] = mode,
            ["amount"] = requiredOptions.ToString(),
            ["profile"] = profile
        }));
    }

    private void DeleteGroupCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 1)
        {
            player.SendErrorMessage(Msg("admin_delgroup_usage"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);

        var removedGroups = RemoveLinesFromFile(MissionGroupsPath, line =>
        {
            var parts = SplitPipe(line);
            return parts.Length >= 1 &&
                parts[0].Equals(missionId, StringComparison.OrdinalIgnoreCase);
        });

        var removedRequirements = RemoveLinesFromFile(MissionRequirementsPath, line =>
        {
            var parts = SplitPipe(line);
            return parts.Length >= 1 &&
                parts[0].Equals(missionId, StringComparison.OrdinalIgnoreCase);
        });

        ReloadFiles();

        if (removedGroups <= 0 && removedRequirements <= 0)
        {
            player.SendErrorMessage(Msg("admin_nothing_deleted"));
            return;
        }

        player.SendSuccessMessage(Msg("admin_delgroup_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["groups"] = removedGroups.ToString(),
            ["requirements"] = removedRequirements.ToString()
        }));
    }

    private void AddRequirementCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 3)
        {
            player.SendErrorMessage(Msg("admin_addreq_usage"));
            return;
        }

        var heldItem = GetHeldItem(player);

        if (heldItem == null || heldItem.IsAir || heldItem.type <= 0)
        {
            player.SendErrorMessage(Msg("admin_item_required"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);
        var optionId = CleanField(args.Parameters[1]);

        if (!int.TryParse(args.Parameters[2], out var amount) || amount <= 0)
        {
            player.SendErrorMessage(Msg("admin_invalid_amount"));
            return;
        }

        if (!GroupMissionExists(missionId))
        {
            player.SendErrorMessage(Msg("admin_group_missing", new Dictionary<string, string?>
            {
                ["missionId"] = missionId
            }));
            return;
        }

        if (RequirementExists(missionId, optionId))
        {
            player.SendErrorMessage(Msg("admin_duplicate_requirement", new Dictionary<string, string?>
            {
                ["missionId"] = missionId,
                ["optionId"] = optionId
            }));
            return;
        }

        var label = args.Parameters.Count >= 4
            ? CleanField(string.Join(" ", args.Parameters.Skip(3)))
            : CleanField(heldItem.Name);

        AppendConfigLine(
            MissionRequirementsPath,
            $"{missionId} | {optionId} | {heldItem.type} | {amount} | {label}"
        );

        ReloadFiles();

        player.SendSuccessMessage(Msg("admin_addreq_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["optionId"] = optionId,
            ["itemName"] = heldItem.Name,
            ["itemId"] = heldItem.type.ToString(),
            ["amount"] = amount.ToString(),
            ["label"] = label
        }));
    }

    private void DeleteRequirementCommand(CommandArgs args)
    {
        var player = args.Player;

        if (player == null || !player.Active)
        {
            return;
        }

        if (args.Parameters.Count < 2)
        {
            player.SendErrorMessage(Msg("admin_delreq_usage"));
            return;
        }

        var missionId = CleanField(args.Parameters[0]);
        var optionId = CleanField(args.Parameters[1]);

        var removed = RemoveLinesFromFile(MissionRequirementsPath, line =>
        {
            var parts = SplitPipe(line);
            return parts.Length >= 2 &&
                parts[0].Equals(missionId, StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals(optionId, StringComparison.OrdinalIgnoreCase);
        });

        ReloadFiles();

        if (removed <= 0)
        {
            player.SendErrorMessage(Msg("admin_nothing_deleted"));
            return;
        }

        player.SendSuccessMessage(Msg("admin_delreq_success", new Dictionary<string, string?>
        {
            ["missionId"] = missionId,
            ["optionId"] = optionId,
            ["count"] = removed.ToString()
        }));
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

        args.Player.SendSuccessMessage(Msg("admin_enabled"));
    }

    private void DisableCommand(CommandArgs args)
    {
        lock (_sync)
        {
            _config.Enabled = false;
            SaveConfig();
        }

        args.Player.SendSuccessMessage(Msg("admin_disabled"));
    }

    private void StatusCommand(CommandArgs args)
    {
        var status = _config.Enabled ? Msg("status_enabled") : Msg("status_disabled");
        var consume = _config.ConsumeItemOnSuccess ? Msg("status_yes") : Msg("status_no");

        args.Player.SendInfoMessage(Msg("status_line_system", new Dictionary<string, string?>
        {
            ["status"] = status
        }));

        args.Player.SendInfoMessage(Msg("status_line_profile", new Dictionary<string, string?>
        {
            ["profile"] = _config.DefaultProfile
        }));

        args.Player.SendInfoMessage(Msg("status_line_consume", new Dictionary<string, string?>
        {
            ["consume"] = consume
        }));

        args.Player.SendInfoMessage(Msg("status_line_simple", new Dictionary<string, string?>
        {
            ["count"] = _missionsByItemId.Values.Sum(list => list.Count).ToString()
        }));

        args.Player.SendInfoMessage(Msg("status_line_group", new Dictionary<string, string?>
        {
            ["count"] = _missionGroups.Count.ToString()
        }));
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
            player.SendErrorMessage(Msg("item_no_valid_hand"));
            return;
        }

        player.SendInfoMessage(Msg("item_info", new Dictionary<string, string?>
        {
            ["itemName"] = item.Name,
            ["itemId"] = item.type.ToString(),
            ["amount"] = item.stack.ToString()
        }));
    }

    private void ReloadCommand(CommandArgs args)
    {
        try
        {
            EnsureFiles();
            ReloadFiles();

            args.Player.SendSuccessMessage(Msg("reload_success"));

            args.Player.SendInfoMessage(Msg("reload_status", new Dictionary<string, string?>
            {
                ["status"] = _config.Enabled ? Msg("status_enabled") : Msg("status_disabled")
            }));

            args.Player.SendInfoMessage(Msg("reload_counts", new Dictionary<string, string?>
            {
                ["profiles"] = _profiles.Count.ToString(),
                ["simple"] = _missionsByItemId.Values.Sum(list => list.Count).ToString(),
                ["groups"] = _missionGroups.Count.ToString(),
                ["players"] = _players.Count.ToString()
            }));
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[TerrariaMissionBridge] Error recargando: {ex}");
            args.Player.SendErrorMessage(Msg("reload_error"));
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

    private sealed class SimpleMissionCandidate
    {
        public MissionEntry Mission { get; set; } = new MissionEntry();
        public BridgeProfile Profile { get; set; } = new BridgeProfile();
    }

    private sealed class GroupMissionCandidate
    {
        public MissionGroup Group { get; set; } = new MissionGroup();
        public BridgeProfile Profile { get; set; } = new BridgeProfile();
        public GroupDeliveryPlan Plan { get; set; } = new GroupDeliveryPlan();
    }

    private sealed class DeliveryAttemptResult
    {
        public bool Completed { get; set; }
        public bool ShouldTryNext { get; set; }

        public static DeliveryAttemptResult CompletedResult()
        {
            return new DeliveryAttemptResult
            {
                Completed = true,
                ShouldTryNext = false
            };
        }

        public static DeliveryAttemptResult TryNext()
        {
            return new DeliveryAttemptResult
            {
                Completed = false,
                ShouldTryNext = true
            };
        }

        public static DeliveryAttemptResult Stop()
        {
            return new DeliveryAttemptResult
            {
                Completed = false,
                ShouldTryNext = false
            };
        }
    }

    private sealed class LinkVerifyRequest
    {
        [JsonPropertyName("guildId")]
        public string GuildId { get; set; } = "";

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";
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

        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("missionTitle")]
        public string? MissionTitle { get; set; }

        [JsonPropertyName("rewardText")]
        public string? RewardText { get; set; }

        public int StatusCode { get; set; }
    }
}