# TerrariaMissionBridge

Plugin de TShock para conectar Terraria con CLEconomy.

## Comandos

- `/discord <codigo>`: vincula el personaje actual con el Discord que genero el codigo en CLEconomy.
- `/entregar`: entrega el item de la mano si coincide con una mision.
- `/mbitem`: muestra el ID del item en la mano.
- `/mbreload`: recarga archivos de configuracion.

## Archivos

El plugin crea/usa una carpeta:

`tshock/TerrariaMissionBridge/`

Con:

- `config.txt`
- `profiles.txt`
- `missions.txt`
- `players.txt`
- `mission_groups.txt`
- `mission_requirements.txt`
- `messages.txt`

## Configuracion minima

En `profiles.txt` agrega una linea por perfil:

```text
main | http://IP_O_DOMINIO_DEL_BOT:3000/terraria/mission-complete | MISMO_TERRARIA_BRIDGE_SECRET | ID_DEL_SERVIDOR_DISCORD
```

El endpoint debe terminar en `/terraria/mission-complete`. El plugin calcula automaticamente:

- `/terraria/mission-prepare`
- `/terraria/link-verify`

`players.txt` no se edita a mano normalmente: se llena cuando un jugador usa `/discord <codigo>`.
